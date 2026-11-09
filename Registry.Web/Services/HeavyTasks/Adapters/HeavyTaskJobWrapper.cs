using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Builds;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Hangfire entry point that executes a resolved <see cref="IHeavyTool"/> on the
/// <c>tasks</c> queue. Creates the per-task work directory, bridges
/// progress/log to <c>JobIndex</c>, persists artifact/error metadata (the error type and the message of
/// <see cref="JobLogText.UserMessage(Exception?, string?)"/>) and schedules
/// TTL cleanup of the work directory.
/// </summary>
public sealed class HeavyTaskJobWrapper : DdbRetriedJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHeavyToolRegistry _registry;
    private readonly ProcessingPlatformSettings _settings;
    private readonly TaskWorkDirectories _workDirs;
    private readonly FileJobs _files;
    private readonly ILogger<HeavyTaskJobWrapper> _log;
    private readonly ILogger<HangfireProgressSink> _sinkLog;

    public HeavyTaskJobWrapper(
        IServiceScopeFactory scopeFactory,
        IHeavyToolRegistry registry,
        IOptions<ProcessingPlatformSettings> platformSettings,
        TaskWorkDirectories workDirs,
        FileJobs files,
        ILogger<HeavyTaskJobWrapper> log,
        ILogger<HangfireProgressSink>? sinkLog = null)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _settings = platformSettings.Value;
        _workDirs = workDirs;
        _files = files;
        _log = log;
        _sinkLog = sinkLog ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HangfireProgressSink>.Instance;
    }

    // The retry policy is the class's (DdbRetriedJob): transient DroneDB contention gets a backoff
    // chain, anything else fails fast.
    // No [Queue]: BuildQueueRoutingFilter routes this job to "tasks" or "builds" by tool.
    public async Task Run(string toolId, string toolVersion, string requestJson,
        PerformContext ctx, CancellationToken cancellationToken)
    {
        var taskId = ctx.BackgroundJob.Id;

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var indexWriter = sp.GetRequiredService<IJobIndexWriter>();

        // A task that cannot even start (its tool gone, its request unreadable, its dataset deleted) failed too: it
        // records why, as one that fails while it runs does
        Prepared prepared;
        try
        {
            prepared = await PrepareAsync(toolId, toolVersion, requestJson, ctx, sp, taskId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Task {TaskId} ({ToolId}) could not start", taskId, toolId);
            if (!WillRetry(ex, ctx))
                await RecordErrorAsync(indexWriter, taskId, ex, JobLogText.UserMessage(ex, null));
            throw;
        }

        var (tool, request, workDir, ddb, execCtx) = prepared;
        var processor = sp.GetRequiredService<IBackgroundJobsProcessor>();
        var logBuffer = new LogRingBuffer(_settings.LogTailMaxLines, _settings.LogTailMaxBytes);
        await using var progress = new HangfireProgressSink(ctx, _scopeFactory, logBuffer, taskId,
            _settings.ProgressUpdateThrottleSeconds, _sinkLog);

        // The work folder has one owner from here: kept with its TTL delete when the tool produced an
        // artifact, deleted on every other outcome
        var keepWorkDir = false;
        try
        {
            if (workDir is not null)
                _workDirs.Create(workDir);

            await tool.ValidateAsync(request, execCtx, cancellationToken);
            var artifact = await tool.ExecuteAsync(request, execCtx, progress, cancellationToken);
            await progress.CompleteAsync();

            if (artifact is not null)
            {
                await indexWriter.UpdateArtifactAsync(taskId, artifact.SizeBytes, artifact.Sha256, cancellationToken);

                if (workDir is not null)
                {
                    processor.Schedule<FileJobs>(
                        j => j.Delete(workDir, null),
                        TimeSpan.FromHours(Math.Max(1, _settings.ArtifactTtlHours)));
                    keepWorkDir = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await progress.CompleteAsync();
            throw;
        }
        catch (Exception ex)
        {
            // The whole exception, with its stack, for the administrator; the task keeps the message a reader of the
            // task may see (no server path), and its log ends with it
            _log.LogWarning(ex, "Task {TaskId} ({ToolId}) failed", taskId, toolId);
            var message = JobLogText.UserMessage(ex, ddb.DatasetFolderPath);
            // A failure the retry policy runs again (the index or the build lock busy, a retryable libddb error) is not
            // the task's end: its log says it waits, and no error is recorded until the last attempt fails.
            // The tool has returned: no progress report runs any more, so the buffer has no other writer
            var retried = WillRetry(ex, ctx);
            logBuffer.Append(retried ? $"Retrying: {message}" : $"Failed: {message}", retried ? "warn" : "error");
            await progress.CompleteAsync();
            if (!retried)
                await RecordErrorAsync(indexWriter, taskId, ex, message);
            throw;
        }
        finally
        {
            if (!keepWorkDir)
                _workDirs.Delete(workDir);
        }
    }

    /// <summary>What a task needs to run, resolved before its work folder exists.</summary>
    private sealed record Prepared(IHeavyTool Tool, HeavyToolRequest Request, string? WorkDir, IDDB Ddb,
        HeavyToolExecutionContext ExecCtx);

    private async Task<Prepared> PrepareAsync(string toolId, string toolVersion, string requestJson, PerformContext ctx,
        IServiceProvider sp, string taskId, CancellationToken cancellationToken)
    {
        var tool = _registry.Resolve(toolId, toolVersion)
                   ?? throw new InvalidOperationException($"Tool '{toolId}' v'{toolVersion}' is not registered.");

        var request = JsonSerializer.Deserialize<HeavyToolRequest>(requestJson)
                      ?? throw new InvalidOperationException("Invalid heavy task request payload.");

        var workDir = _workDirs.PathFor(taskId, tool.ProducesArtifact);

        var db = sp.GetRequiredService<RegistryContext>();
        var ddbManager = sp.GetRequiredService<IDdbManager>();
        var builds = sp.GetRequiredService<DdbJobs>();

        // Everything the task needs is resolved before its work folder exists: a dataset deleted
        // while the task waited leaves nothing behind
        var ds = await db.Datasets.AsNoTracking().Include(d => d.Organization)
                     .FirstOrDefaultAsync(d => d.Slug == request.DsSlug && d.Organization.Slug == request.OrgSlug,
                         cancellationToken)
                 ?? throw new InvalidOperationException(
                     $"Dataset '{request.OrgSlug}/{request.DsSlug}' not found for task {taskId}.");
        var ddb = ddbManager.Get(request.OrgSlug, ds.InternalRef);

        var execCtx = new HeavyToolExecutionContext(ddb, new DatasetKey(request.OrgSlug, ds.InternalRef), _log,
            new TaskRun(taskId, workDir), ctx, sp.GetRequiredService<BuildScheduler>(), builds,
            await QueuedBeforeOverwriteRuleAsync(db, taskId, cancellationToken));
        return new Prepared(tool, request, workDir, ddb, execCtx);
    }

    // A task record without output paths was written before the tools declared them (U-103): its request, without
    // the overwrite field, replaced an existing output. A missing record is not one of them.
    internal static async Task<bool> QueuedBeforeOverwriteRuleAsync(RegistryContext db, string taskId,
        CancellationToken ct)
    {
        var row = await db.JobIndices.AsNoTracking()
            .Where(j => j.JobId == taskId)
            .Select(j => new { j.OutputPathsJson })
            .FirstOrDefaultAsync(ct);
        return row is { OutputPathsJson: null };
    }

    /// <summary>
    /// Whether Hangfire runs this job again after <paramref name="error"/>: the job's retry policy (the
    /// <see cref="AutomaticRetryAttribute"/> of <see cref="Run"/>, else the class's, <see cref="DdbRetriedJob"/>) retries
    /// it and has attempts left. The same rule as the filter, read from the attribute it reads.
    /// </summary>
    internal bool WillRetry(Exception error, PerformContext ctx)
    {
        var policy = GetType().GetMethod(nameof(Run))?.GetCustomAttribute<AutomaticRetryAttribute>()
                     ?? GetType().GetCustomAttribute<AutomaticRetryAttribute>(inherit: true);
        if (policy is null || policy.Attempts <= 0)
            return false;
        if (policy.OnlyOn is { Length: > 0 } onlyOn && !onlyOn.Any(type => type.IsInstanceOfType(error)))
            return false;

        int retryCount;
        try
        {
            retryCount = ctx.GetJobParameter<int>("RetryCount");
        }
        catch (Exception ex)
        {
            // Cannot tell: the failure is taken as final, so it is recorded
            _log.LogDebug(ex, "Cannot read the retry count of task {TaskId}", ctx.BackgroundJob.Id);
            return false;
        }

        return retryCount < policy.Attempts;
    }

    // Recorded with its own short token, not the job's: the failure must be recorded even when the
    // job's token is already cancelled (shutdown), and a failure to record it is logged
    private async Task RecordErrorAsync(IJobIndexWriter indexWriter, string taskId, Exception error, string message)
    {
        using var timeout = new CancellationTokenSource(ErrorRecordTimeout);
        try
        {
            await indexWriter.UpdateErrorAsync(taskId, error.GetType().Name, message, timeout.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cannot record the failure ({ErrorType}) of task {TaskId}", error.GetType().Name, taskId);
        }
    }

    /// <summary>How long recording a task's failure may take.</summary>
    private static readonly TimeSpan ErrorRecordTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hangfire-scheduled TTL cleanup of a finished task's work directory, kept for the jobs
    /// scheduled by earlier releases: new schedules use <see cref="FileJobs.Delete"/>.
    /// </summary>
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public void DeleteWorkDirJob(string workDir) => _files.Delete(workDir, null);
}
