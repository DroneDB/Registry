#nullable enable
using System;
using System.IO;
using System.Linq;
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
using Registry.Web.Utilities;
using Serilog;
using Registry.Web.Services.Builds;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Hangfire entry point that executes a resolved <see cref="IHeavyTool"/> on the
/// <c>tasks</c> queue. Creates the per-task work directory, bridges
/// progress/log to <c>JobIndex</c>, persists artifact/error metadata and schedules
/// TTL cleanup of the work directory.
/// </summary>
public sealed class HeavyTaskJobWrapper : DdbRetriedJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHeavyToolRegistry _registry;
    private readonly ProcessingPlatformSettings _settings;
    private readonly string _tempPath;
    private readonly IFileSystem _fs;
    private readonly FileJobs _files;
    private readonly ILogger<HeavyTaskJobWrapper> _log;
    private readonly ILogger<HangfireProgressSink> _sinkLog;

    public HeavyTaskJobWrapper(
        IServiceScopeFactory scopeFactory,
        IHeavyToolRegistry registry,
        IOptions<ProcessingPlatformSettings> platformSettings,
        IScratchFileProvider scratch,
        IFileSystem fs,
        FileJobs files,
        ILogger<HeavyTaskJobWrapper> log,
        ILogger<HangfireProgressSink>? sinkLog = null)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _settings = platformSettings.Value;
        _tempPath = scratch.Root;
        _fs = fs;
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

        var tool = _registry.Resolve(toolId, toolVersion)
                   ?? throw new InvalidOperationException($"Tool '{toolId}' v'{toolVersion}' is not registered.");

        var request = JsonSerializer.Deserialize<HeavyToolRequest>(requestJson)
                      ?? throw new InvalidOperationException("Invalid heavy task request payload.");

        var workDir = tool.ProducesArtifact
            ? TaskJobHelpers.ArtifactDirectory(taskId, _tempPath)
              ?? throw new InvalidOperationException($"Invalid task id '{taskId}'.")
            : null;

        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<RegistryContext>();
        var ddbManager = sp.GetRequiredService<IDdbManager>();
        var indexWriter = sp.GetRequiredService<IJobIndexWriter>();
        var processor = sp.GetRequiredService<IBackgroundJobsProcessor>();
        var builds = sp.GetRequiredService<DdbJobs>();

        // Everything the task needs is resolved before its work folder exists: a dataset deleted
        // while the task waited leaves nothing behind
        var ds = await db.Datasets.AsNoTracking().Include(d => d.Organization)
                     .FirstOrDefaultAsync(d => d.Slug == request.DsSlug && d.Organization.Slug == request.OrgSlug,
                         cancellationToken)
                 ?? throw new InvalidOperationException(
                     $"Dataset '{request.OrgSlug}/{request.DsSlug}' not found for task {taskId}.");
        var ddb = ddbManager.Get(request.OrgSlug, ds.InternalRef);

        var execCtx = new HeavyToolExecutionContext(ddb, new DatasetKey(request.OrgSlug, ds.InternalRef), null, _log,
            taskId, workDir, ctx, sp.GetRequiredService<IBuildScheduler>(), builds);
        var logBuffer = new LogRingBuffer(_settings.LogTailMaxLines, _settings.LogTailMaxBytes);
        await using var progress = new HangfireProgressSink(ctx, _scopeFactory, logBuffer, taskId,
            _settings.ProgressUpdateThrottleSeconds, _sinkLog);

        // The work folder has one owner from here: kept with its TTL delete when the tool produced an
        // artifact, deleted on every other outcome
        var keepWorkDir = false;
        try
        {
            if (workDir is not null)
                _fs.FolderCreate(workDir);

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
            await progress.CompleteAsync();
            await RecordErrorAsync(indexWriter, taskId, ex);
            throw;
        }
        finally
        {
            if (!keepWorkDir)
                DeleteWorkDir(workDir);
        }
    }

    // Recorded with its own short token, not the job's: the failure must be recorded even when the
    // job's token is already cancelled (shutdown), and a failure to record it is logged
    private async Task RecordErrorAsync(IJobIndexWriter indexWriter, string taskId, Exception error)
    {
        using var timeout = new CancellationTokenSource(ErrorRecordTimeout);
        try
        {
            await indexWriter.UpdateErrorAsync(taskId, error.GetType().Name, timeout.Token);
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
    /// scheduled before RQ-10 (D4): new schedules use <see cref="FileJobs.Delete"/>.
    /// </summary>
    [AutomaticRetry(Attempts = 1, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public void DeleteWorkDirJob(string workDir) => _files.Delete(workDir, null);

    private void DeleteWorkDir(string? workDir) =>
        _fs.TryDelete(workDir, ex => _log.LogWarning(ex, "Failed to delete task work directory '{Dir}'", workDir));
}
