using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>
/// The one way a queued or running task is stopped: by the user who cancels it (<see cref="TaskLifecycle.CancelAsync"/>,
/// with its access check and its <c>CancelledBy</c>), by the delete of its dataset and by the cleanup that follows.
/// The cancel is first recorded in the task's write record, so a task that adds dataset files can no longer commit; then
/// its job is deleted from the job store. A task that never started (or whose job the store lost) is rolled back here,
/// a running one by its worker when it sees its job deleted.
/// </summary>
public sealed class TaskCanceller
{
    private readonly IBackgroundJobsProcessor _processor;
    private readonly IJobIndexWriter _writer;
    private readonly ITaskWriteRecordStore _records;
    private readonly ITaskRollback _rollback;
    private readonly ILogger<TaskCanceller> _logger;

    /// <summary>
    /// The states of a job no worker has started (deleted from one of them, it never runs), in the order a job goes
    /// through them: a job that moves on while they are tried is found in a later one (or in the second pass).
    /// </summary>
    private static readonly string[] NotStartedStates =
    [
        IndexingState.StateName, AwaitingState.StateName, ScheduledState.StateName, EnqueuedState.StateName
    ];

    public TaskCanceller(IBackgroundJobsProcessor processor, IJobIndexWriter writer, ITaskWriteRecordStore records,
        ITaskRollback rollback, ILogger<TaskCanceller> logger)
    {
        _processor = processor;
        _writer = writer;
        _records = records;
        _rollback = rollback;
        _logger = logger;
    }

    /// <summary>
    /// Stops task <paramref name="jobId"/> of <paramref name="dsSlug"/> of <paramref name="orgSlug"/>: true when it was
    /// stopped, false when it ended by itself meanwhile or has committed its result (it is then finishing, and is left
    /// to end).
    /// </summary>
    public async Task<bool> CancelAsync(string jobId, string orgSlug, string dsSlug, CancellationToken ct = default) =>
        await RequestAsync(jobId, ct) != TaskCancelRequestResult.Committed && await EndAsync(jobId, orgSlug, dsSlug, ct);

    /// <summary>
    /// Records the cancel in the task's write record, before its job is deleted: from here on the task cannot commit.
    /// <see cref="TaskCancelRequestResult.Committed"/> when it already has: it can no longer be cancelled.
    /// </summary>
    public Task<TaskCancelRequestResult> RequestAsync(string jobId, CancellationToken ct = default) =>
        _records.RequestCancelAsync(jobId, ct);

    /// <summary>Ends the job of a task whose cancel is recorded (<see cref="RequestAsync"/>); false when it ended by itself meanwhile.</summary>
    public async Task<bool> EndAsync(string jobId, string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        // Deleted before a worker took it: it never runs, so nothing else rolls it back. Twice: a job that moved from one
        // of these states to another while they were tried
        if (DeleteJob(jobId, NotStartedStates) || DeleteJob(jobId, NotStartedStates))
        {
            await RollBackAsync(jobId);
            return true;
        }

        // Running: its worker rolls back when it sees its job deleted. Only from Processing, never from any state: a job
        // that ended meanwhile (Succeeded, Failed) keeps its end
        if (DeleteJob(jobId, [ProcessingState.StateName]))
            return true;

        // Ended meanwhile, or in no state the cancel knows: the task stays as it is. Only a task whose job the store no
        // longer has ends here
        if (!JobLost(jobId))
            return false;

        var ended = await _writer.UpdateStateIfCurrentAsync(jobId, TaskStateCatalog.Active,
            DeletedState.StateName, DateTime.UtcNow, ct);
        if (ended)
        {
            _logger.LogWarning("Cancelled task {JobId} of {Org}/{Ds}, whose job the job store no longer has",
                jobId, orgSlug, dsSlug);
            await RollBackAsync(jobId);
        }

        return ended;
    }

    // The task is cancelled whatever the rollback does: one that fails is logged, and the startup sweep finishes it
    private Task RollBackAsync(string jobId) =>
        BestEffort.RunAsync(() => _rollback.RollBackAsync(jobId, null, CancellationToken.None),
            ex => _logger.LogError(ex, "Cannot roll back the cancelled task {JobId}", jobId));

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
    // numbering in a GUID store) is not there either (Unknown)
    private bool JobLost(string jobId) => _processor.GetJobStatus(jobId) is JobStatus.Unknown or JobStatus.Deleted;
}
