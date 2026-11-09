using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>Ending a task and clearing the history (out of <c>TasksManager</c>).</summary>
public sealed class TaskLifecycle
{
    private readonly IDatasetAccessResolver _access;
    private readonly TaskAuthorizer _authorizer;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly IJobIndexWriter _writer;
    private readonly TaskArtifactStore _artifacts;
    private readonly ITaskWriteRecordStore _records;
    private readonly ITaskRollback _rollback;
    private readonly ILogger<TaskLifecycle> _logger;

    /// <summary>
    /// The states of a job no worker has started (deleted from one of them, it never runs), in the order a job goes
    /// through them: a job that moves on while they are tried is found in a later one (or in the second pass).
    /// </summary>
    private static readonly string[] NotStartedStates =
    [
        IndexingState.StateName, AwaitingState.StateName, ScheduledState.StateName, EnqueuedState.StateName
    ];

    /// <summary>The answer to the cancel of a task whose write record is committed (D-002, FR-XC-27).</summary>
    public const string CommittedMessage =
        "This task can no longer be cancelled: it has already published its result";

    public TaskLifecycle(IDatasetAccessResolver access, TaskAuthorizer authorizer, IBackgroundJobsProcessor processor,
        IJobIndexWriter writer, TaskArtifactStore artifacts, ITaskWriteRecordStore records, ITaskRollback rollback,
        ILogger<TaskLifecycle> logger)
    {
        _access = access;
        _authorizer = authorizer;
        _processor = processor;
        _writer = writer;
        _artifacts = artifacts;
        _records = records;
        _rollback = rollback;
        _logger = logger;
    }

    /// <summary>Permanently removes the dataset's concluded tasks (optionally of one tool) and purges their artifacts; the number removed.</summary>
    public async Task<int> ClearAsync(string orgSlug, string dsSlug, string? toolId, CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        // Permanently remove concluded (Succeeded/Failed/Deleted) tasks from the history
        // and purge any artifacts they produced, instead of merely flipping them to a
        // terminal "Deleted" state where they would linger in the list forever.
        var removedIds = await _writer.DeleteTerminalForDatasetAsync(orgSlug, dsSlug, toolId, ct);
        foreach (var jobId in removedIds)
            _artifacts.Delete(jobId);

        _logger.LogInformation("Cleared {Count} concluded task(s) for {Org}/{Ds}", removedIds.Count, orgSlug, dsSlug);
        return removedIds.Count;
    }

    /// <summary>
    /// Cancels a queued or running task: deletes its job from the job store. A task whose job the store no
    /// longer has (lost when an InMemory store restarted) can never run, so it ends here too (Deleted); false
    /// only when the task ended meanwhile. A task that adds dataset files is rolled back from its write record
    /// (FR-XC-27): the cancel request is recorded first, so the task can no longer commit; a task that never started
    /// (or lost its job) is rolled back here, a running one by its worker when it sees its job deleted.
    /// </summary>
    /// <exception cref="BadRequestException">The task has already ended, or committed its result.</exception>
    public async Task<bool> CancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Write, ct);
        if (TaskStateCatalog.IsTerminal(job.CurrentState))
            throw new BadRequestException(
                $"The task has already ended ({job.CurrentState}); only a queued or running task can be cancelled",
                noRetry: true);

        // Before its job is deleted: from here on the task cannot commit, and a task that committed is not cancelled
        if (await _records.RequestCancelAsync(job.JobId, ct) == TaskCancelRequestResult.Committed)
            throw new BadRequestException(CommittedMessage, noRetry: true);

        // Deleted before a worker took it: it never runs, so nothing else rolls it back. Twice: a job that moved from one
        // of these states to another while they were tried
        if (DeleteJob(job.JobId, NotStartedStates) || DeleteJob(job.JobId, NotStartedStates))
        {
            await RollBackAsync(job.JobId);
            return true;
        }

        // Running: its worker rolls back when it sees its job deleted. Only from Processing, never from any state: a job
        // that ended meanwhile (Succeeded, Failed) keeps its end
        if (DeleteJob(job.JobId, [ProcessingState.StateName]))
            return true;

        // Ended meanwhile, or in no state the cancel knows: the task stays as it is. Only a task whose job the store no
        // longer has ends here
        if (!JobLost(job.JobId))
            return false;

        var ended = await _writer.UpdateStateIfCurrentAsync(job.JobId, TaskStateCatalog.Active,
            DeletedState.StateName, DateTime.UtcNow, ct);
        if (ended)
        {
            _logger.LogWarning("Cancelled task {JobId} of {Org}/{Ds}, whose job the job store no longer has",
                job.JobId, orgSlug, dsSlug);
            await RollBackAsync(job.JobId);
        }

        return ended;
    }

    // The task is cancelled whatever the rollback does: one that fails is logged, and the startup sweep finishes it
    private async Task RollBackAsync(string jobId)
    {
        try
        {
            await _rollback.RollBackAsync(jobId, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot roll back the cancelled task {JobId}", jobId);
        }
    }

    // True when the job was in one of the states and is deleted
    private bool DeleteJob(string jobId, string[] fromStates)
    {
        try
        {
            foreach (var state in fromStates)
                if (_processor.Delete(jobId, state))
                    return true;
            return false;
        }
        catch (FormatException ex)
        {
            _logger.LogDebug(ex, "Job id {JobId} is not one of the job store", jobId);
            return false;
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug(ex, "Job id {JobId} is not one of the job store", jobId);
            return false;
        }
    }

    // Whether the store has no such job (or a deleted one); a job id the store cannot read (a number of the earlier
    // numbering in a GUID store) is not there either
    private bool JobLost(string jobId)
    {
        try
        {
            return _processor.GetJobStatus(jobId) is JobStatus.Unknown or JobStatus.Deleted;
        }
        catch (FormatException ex)
        {
            _logger.LogDebug(ex, "Job id {JobId} is not one of the job store", jobId);
            return true;
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug(ex, "Job id {JobId} is not one of the job store", jobId);
            return true;
        }
    }

    /// <summary>Removes one concluded task from the history and purges its artifacts.</summary>
    /// <exception cref="BadRequestException">The task has not concluded.</exception>
    public async Task<bool> DeleteAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Write, ct);

        var (deleted, _, _) = await _writer.DeleteTerminalJobByIdAsync(job.JobId, ct);
        if (!deleted)
            throw new BadRequestException(
                $"Task '{job.CurrentState}' cannot be deleted; only concluded tasks can be removed from history");

        // Best-effort artifact cleanup (same as Clear)
        _artifacts.Delete(job.JobId);

        _logger.LogInformation("Deleted terminal JobIndex record for job {JobId} in {Org}/{Ds}", job.JobId, orgSlug,
            dsSlug);
        return deleted;
    }
}
