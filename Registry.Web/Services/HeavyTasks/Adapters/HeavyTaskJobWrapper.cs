using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Hangfire.States;
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
        // records why, as one that fails while it runs does, and what was created for it (a dataset, at submit) goes
        Prepared prepared;
        try
        {
            prepared = await PrepareAsync(toolId, toolVersion, requestJson, ctx, sp, taskId, cancellationToken);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            var final = false;
            if (!cancelled)
            {
                _log.LogWarning(ex, "Task {TaskId} ({ToolId}) could not start", taskId, toolId);
                final = !WillRetry(ex, ctx);
                if (final)
                    await RecordErrorAsync(indexWriter, taskId, ex, JobLogText.UserMessage(ex, null));
            }

            // A tool that adds no dataset files has no record; an unknown tool may (a dataset created for it)
            if (_registry.Resolve(toolId, toolVersion) is not { AddsDatasetFiles: false })
                await EndWriteRecordAsync(sp, taskId, null, final, cancellationToken);
            throw;
        }

        var (tool, request, workDir, ddb, execCtx, recorder) = prepared;
        using var ownedRecorder = recorder;
        var processor = sp.GetRequiredService<IBackgroundJobsProcessor>();
        var logBuffer = new LogRingBuffer(_settings.LogTailMaxLines, _settings.LogTailMaxBytes);
        await using var progress = new HangfireProgressSink(ctx, _scopeFactory, logBuffer, taskId,
            _settings.ProgressUpdateThrottleSeconds, _sinkLog);

        // The work folder has one owner from here: kept with its TTL delete when the tool produced an
        // artifact, deleted on every other outcome
        var keepWorkDir = false;
        // A failure that ends the task (not retried): what it wrote is rolled back (FR-XC-27)
        var failedForGood = false;
        try
        {
            if (recorder.Enabled)
                await StartAttemptAsync(sp, taskId, ctx, tool, request, ddb, cancellationToken);

            if (workDir is not null)
                _workDirs.Create(workDir);

            await tool.ValidateAsync(request, execCtx, cancellationToken);
            HeavyToolArtifact? artifact;
            try
            {
                artifact = await tool.ExecuteAsync(request, execCtx, progress, cancellationToken);
                if (recorder.Enabled)
                    await CommitAsync(sp, taskId, recorder, cancellationToken);
            }
            catch (TaskWriteRecordClosedException) when (recorder.Enabled)
            {
                // A commit (the wrapper's or the tool's own, before it deletes its sources) or a write refused by a
                // cancel request or a rollback: the cancel's, before it deleted the job
                EndCancelledRun(sp, taskId);
                throw;
            }

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
        catch (Exception ex) when (recorder.Enabled && JobDeleted(sp, taskId))
        {
            // The user cancelled the task (its job is deleted) and it stopped with another exception than a
            // cancellation: its commit or a write was refused by the cancel or its rollback. A cancel, not a failure:
            // no error is recorded, and the end of its write record rolls it back
            _log.LogInformation(ex, "Task {TaskId} ({ToolId}) stopped after it was cancelled", taskId, toolId);
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
            {
                failedForGood = true;
                await RecordErrorAsync(indexWriter, taskId, ex, message);
            }

            throw;
        }
        finally
        {
            if (!keepWorkDir)
                _workDirs.Delete(workDir);
            if (recorder.Enabled)
                await EndWriteRecordAsync(sp, taskId, recorder, failedForGood, cancellationToken);
        }
    }

    /// <summary>
    /// The attempt rule of the write record (<see cref="ITaskRollback.StartAttemptAsync"/>): what a lost run of this
    /// attempt wrote goes before the attempt writes; a task lost after it committed does not run again, and fails with
    /// the sources it was to delete after its commit that remain (FR-XC-27).
    /// </summary>
    private async Task StartAttemptAsync(IServiceProvider sp, string taskId, PerformContext ctx, IHeavyTool tool,
        HeavyToolRequest request, IDDB ddb, CancellationToken ct)
    {
        var start = await sp.GetRequiredService<ITaskRollback>().StartAttemptAsync(taskId, RetryCountOf(ctx), ct);
        if (start == TaskAttemptStart.LostRunRolledBack)
            _log.LogWarning("Task {TaskId} runs again after a run that was lost: what that run wrote was deleted", taskId);
        else if (start == TaskAttemptStart.CommittedLost)
            throw CommittedLost(taskId, tool, request, ddb);
    }

    // The sources the tool deletes after its commit that are still in the dataset folder (an indexed entry whose file
    // is gone was deleted: the tools remove the entry, then the file)
    private TaskCommittedLostException CommittedLost(string taskId, IHeavyTool tool, HeavyToolRequest request, IDDB ddb)
    {
        IReadOnlyList<string> sources;
        try
        {
            sources = tool.SourcesDeletedAfterCommit(request);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cannot read the sources task {TaskId} deletes after its commit", taskId);
            return new TaskCommittedLostException();
        }

        return TaskCommittedLostException.For(sources, path =>
        {
            try
            {
                return File.Exists(ddb.GetLocalPath(path));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Cannot tell whether source '{Path}' of task {TaskId} remains", path, taskId);
                return true;
            }
        });
    }

    // The task did all its work: the buffered records are written, and the record is committed, so a cancel that
    // comes now is refused and nothing rolls the task back. A cancel requested before (or a rollback that claimed the
    // record) refuses the commit: the task ends there as cancelled (its job is deleted, EndCancelledRun), with no error
    // and no retry, and is rolled back
    private async Task CommitAsync(IServiceProvider sp, string taskId, TaskWriteRecorder recorder,
        CancellationToken ct)
    {
        await recorder.FlushAsync(ct);
        if (await sp.GetRequiredService<ITaskWriteRecordStore>().MarkCommittedAsync(taskId, CancellationToken.None,
                recorder.CommittedSources))
            return;

        _log.LogInformation("Task {TaskId} finished after a cancel request: it is not committed, and is rolled back",
            taskId);
        throw TaskWriteRecordClosedException.Cancelled(taskId);
    }

    // The record refused the run (a cancel request came between the cancel's record change and its delete of the job,
    // or a rollback claimed it): the job is deleted from Processing here, so that the run ends as the cancel it is
    // (Deleted, no error, no Retry) rather than as a failure. A job that left Processing meanwhile keeps its state
    private void EndCancelledRun(IServiceProvider sp, string taskId)
    {
        try
        {
            if (sp.GetRequiredService<IBackgroundJobsProcessor>().Delete(taskId, ProcessingState.StateName))
                _log.LogInformation("Task {TaskId} was cancelled while it finished: its job is deleted", taskId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cannot delete the job of the cancelled task {TaskId}", taskId);
        }
    }

    /// <summary>
    /// The end of a task's write record. The task is rolled back (FR-XC-27) when the user cancelled it (its job is
    /// Deleted: the Hangfire state is read, since the token also fires when the server stops) or when it failed for
    /// good; never when the server stops, nor before an automatic retry: then the records still buffered are written,
    /// for the retry or for the attempt rule of the next run. Never throws: the outcome of the job is its own.
    /// </summary>
    private async Task EndWriteRecordAsync(IServiceProvider sp, string taskId, TaskWriteRecorder? recorder,
        bool failedForGood, CancellationToken jobToken)
    {
        try
        {
            var deleted = JobDeleted(sp, taskId);
            var stopping = jobToken.IsCancellationRequested && !deleted;
            if (deleted || (failedForGood && !stopping))
            {
                await sp.GetRequiredService<ITaskRollback>().RollBackAsync(taskId, recorder?.Pending,
                    CancellationToken.None);
                return;
            }

            if (recorder != null)
                await recorder.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cannot end the write record of task {TaskId}: the startup sweep or its next run rolls it back",
                taskId);
        }
    }

    // Whether the user deleted (cancelled) the job; a storage that cannot tell is taken as no
    private bool JobDeleted(IServiceProvider sp, string taskId)
    {
        try
        {
            return sp.GetRequiredService<IBackgroundJobsProcessor>().GetJobStatus(taskId) == JobStatus.Deleted;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cannot read the job state of task {TaskId}", taskId);
            return false;
        }
    }

    // Hangfire's retry count of this attempt (0 on the first; unreadable: 0)
    private int RetryCountOf(PerformContext ctx)
    {
        try
        {
            return ctx.GetJobParameter<int>("RetryCount");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cannot read the retry count of task {TaskId}", ctx.BackgroundJob.Id);
            return 0;
        }
    }

    /// <summary>What a task needs to run, resolved before its work folder exists.</summary>
    private sealed record Prepared(IHeavyTool Tool, HeavyToolRequest Request, string? WorkDir, IDDB Ddb,
        HeavyToolExecutionContext ExecCtx, TaskWriteRecorder Recorder);

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
        var dataset = new DatasetKey(request.OrgSlug, ds.InternalRef);

        // Only a tool that adds dataset files records them (FR-XC-27); the sources it deletes after its commit are
        // written with the commit, for a task lost after it (TaskRollback.CommittedLostAsync)
        var recorder = tool.AddsDatasetFiles
            ? TaskWriteRecorder.For(sp.GetRequiredService<ITaskWriteRecordStore>(), taskId, dataset, tool.Id,
                sp.GetRequiredService<TaskEntryChanges>(), SourcesAfterCommit(tool, request, taskId))
            : TaskWriteRecorder.Disabled(taskId, dataset, tool.Id);

        var execCtx = new HeavyToolExecutionContext(ddb, dataset, _log,
            new TaskRun(taskId, workDir), ctx, sp.GetRequiredService<BuildScheduler>(), builds,
            await QueuedBeforeOverwriteRuleAsync(db, taskId, cancellationToken), recorder);
        return new Prepared(tool, request, workDir, ddb, execCtx, recorder);
    }

    // The sources the tool deletes after its commit; none when the request cannot tell (the tool fails on it later)
    private IReadOnlyList<string> SourcesAfterCommit(IHeavyTool tool, HeavyToolRequest request, string taskId)
    {
        try
        {
            return tool.SourcesDeletedAfterCommit(request);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Cannot read the sources task {TaskId} deletes after its commit", taskId);
            return [];
        }
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
