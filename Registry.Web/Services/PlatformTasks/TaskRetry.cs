using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Managers;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>Re-running a failed task (out of <c>TasksManager</c>).</summary>
public sealed class TaskRetry
{
    private readonly TaskAuthorizer _authorizer;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly IJobIndexWriter _writer;
    private readonly IDatasetResolver _datasets;
    private readonly IDdbManager _ddbManager;
    private readonly ITaskWriteRecordStore _records;
    private readonly ILogger<TaskRetry> _logger;
    private readonly IHeavyTaskRunner? _runner;
    private readonly IHttpContextAccessor? _http;

    /// <summary>The answer to the retry of a task whose write record is committed (D-037, FR-XC-27).</summary>
    public const string CommittedMessage =
        "This task cannot be retried: it had already published its result, which is kept";

    /// <summary>The answer to the retry of a task that a retry already started again (a second click).</summary>
    public const string RunsAgainMessage = "The task is already running again; only a failed task can be run again";

    /// <summary>The answer to the retry of a task that is not failed in the job store, or is no longer in it.</summary>
    public const string NotRetriableMessage =
        "Task cannot be retried in its current state or no longer exists in the job store";

    public TaskRetry(TaskAuthorizer authorizer, IBackgroundJobsProcessor processor, IJobIndexWriter writer,
        IDatasetResolver datasets, IDdbManager ddbManager, ITaskWriteRecordStore records, ILogger<TaskRetry> logger,
        IHeavyTaskRunner? runner = null, IHttpContextAccessor? http = null)
    {
        _authorizer = authorizer;
        _processor = processor;
        _writer = writer;
        _datasets = datasets;
        _ddbManager = ddbManager;
        _records = records;
        _logger = logger;
        _runner = runner;
        _http = http;
    }

    /// <summary>
    /// Re-queues a failed task (D-037): a heavy task first passes the submit checks of its tool again
    /// (<see cref="IHeavyTaskRunner.CheckSubmitAsync"/> with its stored request); false when the job store did not
    /// accept it (see the conflict it throws).
    /// </summary>
    /// <exception cref="ConflictException">The task cannot be retried in its state (a succeeded or a cancelled task),
    /// the pending-build sweep owns the retry, or a submit check refused it (an output another task writes).</exception>
    /// <exception cref="HeavyTaskQuotaException">The owner of the task is over a quota.</exception>
    /// <exception cref="Registry.Web.Exceptions.QuotaExceededException">The tool adds files and the owner of the organization
    /// has no storage left for it (507, FR-XC-26).</exception>
    public async Task<bool> RetryAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Write, ct);

        // Only a failed task runs again (D-037): a succeeded one has its result, and a cancelled one is started again
        // from its tool
        if (job.CurrentState == SucceededState.StateName)
            throw new ConflictException("The task has succeeded; only a failed task can be run again");
        if (job.CurrentState == DeletedState.StateName)
            throw new ConflictException(
                "The task was cancelled or deleted; only a failed task can be run again. Start the tool again.");

        // Sweep-ownership rule: dependency-gated builds are re-run by the pending-build sweep;
        // the Retry button must not start a competing run while libddb reports a deferred build.
        if (job.ToolId == HeavyToolIds.Build && await IsBuildPendingSafeAsync(orgSlug, dsSlug, ct))
            throw new ConflictException(
                "A pending build retry is already scheduled for this dataset; the pending-build sweep owns that retry");

        // A task that committed its output never runs again (D-037, FR-XC-27): its output is kept, and its sources
        // may be gone already
        var record = await _records.ReadAsync(job.JobId, ct);
        if (record?.State == TaskWriteState.Committed)
            throw new ConflictException(CommittedMessage);

        // A heavy task runs the submit checks of its tool first, with the parameters it was submitted with: its
        // validation, an output that another active task writes (409), the quota of its owner (507). The re-queue
        // runs under the submit lock, once every check passed. The validation does not refuse the dataset an earlier
        // run of the task created (its write record) as a taken slug: the re-run imports into it again
        var stored = _runner is null ? null : _processor.StoredHeavyRequest(job.JobId);
        if (stored is not null)
        {
            var request = new HeavyTaskSubmitRequest(orgSlug, dsSlug, stored.ToolId, stored.Version, stored.Path,
                stored.Params, Force: true, job.UserId, _http?.HttpContext?.User, job.Hash, job.ParentJobId,
                job.WorkflowExecutionId, record?.CreatedDataset);
            await _runner!.CheckSubmitAsync(request, async (_, token) =>
            {
                await RequeueAsync(job.JobId, job.CurrentState, token);
                return job.JobId;
            }, ownJobId: job.JobId, ct: ct);
            return true;
        }

        await RequeueAsync(job.JobId, job.CurrentState, ct);
        return true;
    }

    // Re-queue is gated by the LIVE state of the job store (Failed-only) - authoritative, since it
    // reads the actual store rather than the lagging display-only JobIndex row. Wipe stale
    // prior-run state (error fields, timestamps, artifacts) only AFTER an accepted transition,
    // so a rejected retry (job purged/expired or state moved on) never destroys the still
    // valid failure diagnostics the user just asked about.
    private async Task RequeueAsync(string jobId, string rowState, CancellationToken ct)
    {
        // The state is read again here, under the submit lock and before anything is reset: a second retry of the same
        // task (a double click, two users) finds the run the first one started and is refused, so it never resets the
        // write record of that run
        var live = LiveStatus(jobId);
        if (live != JobStatus.Failed)
        {
            _logger.LogInformation("Retry of task {JobId} refused: its job is {State}, no longer Failed", jobId, live);
            throw new ConflictException(live is JobStatus.Enqueued or JobStatus.Scheduled or JobStatus.Processing
                or JobStatus.Awaiting
                ? RunsAgainMessage
                : NotRetriableMessage);
        }

        // The write record starts again BEFORE the re-queue, by compare-and-swap (§8.2): the delayed delete of a dataset
        // the task created reads it and keeps the dataset of a task that runs again, and a run that starts at once
        // never has its record replaced. A rejected re-queue puts the record back
        var reset = await ResetWriteRecordAsync(jobId, ct);

        // Taken before the re-queue: the row reset does not replace a failure of the new run stamped after it
        var requeuedAt = DateTime.UtcNow;
        if (_processor.Requeue(jobId))
        {
            await _writer.ResetForRequeueAsync(jobId, requeuedAt, ct);
            if (reset is { } accepted)
                CancelCreatedDatasetDelete(jobId, accepted.Before);
            return;
        }

        if (reset is { } r)
            await RestoreWriteRecordAsync(jobId, r.Before, r.After);

        _logger.LogInformation(
            "Retry of task {JobId} rejected: row state '{State}' (Failed-only guard or job missing in Hangfire)",
            jobId, rowState);
        // State-free message on purpose: the DB row state can lag the guard's live read,
        // so do not assert a state here; details are in the log line above.
        throw new ConflictException(NotRetriableMessage);
    }

    // The live state of the job in the job store; a job the store cannot read is Unknown
    private JobStatus LiveStatus(string jobId)
    {
        try
        {
            return _processor.GetJobStatus(jobId);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Cannot read the state of the job of task {JobId}", jobId);
            return JobStatus.Unknown;
        }
    }

    // The re-run writes into the dataset an earlier run created (photogrammetry reuses it): the delayed delete that the
    // rollback of the failed run scheduled is cancelled while it still waits. One that started meanwhile reads the record,
    // now open, and keeps the dataset (CreatedDatasetRollbackJob)
    private void CancelCreatedDatasetDelete(string jobId, string recordBefore)
    {
        if (TaskWriteRecord.Parse(recordBefore)?.CreatedDatasetDeleteJob is not { } deleteJob)
            return;

        try
        {
            if (_processor.Delete(deleteJob, ScheduledState.StateName))
                _logger.LogInformation("Retry of task {JobId}: the delayed delete {DeleteJob} of the dataset it created " +
                                       "is cancelled", jobId, deleteJob);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Retry of task {JobId}: cannot cancel the delayed delete {DeleteJob} of the dataset " +
                                   "it created; that job keeps the dataset of a task that runs again", jobId, deleteJob);
        }
    }

    // The record of the re-queued run (TaskWriteRecord.ForRequeue); the record before and after, when it changed
    private async Task<(string Before, string After)?> ResetWriteRecordAsync(string jobId, CancellationToken ct)
    {
        string? before = null;
        var change = await _records.ChangeAsync(jobId, r =>
        {
            before = r?.ToJson();
            return TaskWriteRecord.ForRequeue(r);
        }, ct);

        return change is { Changed: true, Record: { } after } && before != null ? (before, after.ToJson()) : null;
    }

    // The re-queue was rejected: the record goes back to what it was, unless something changed it since
    private async Task RestoreWriteRecordAsync(string jobId, string before, string after)
    {
        try
        {
            await _records.ChangeAsync(jobId, r => r?.ToJson() == after ? TaskWriteRecord.Parse(before) : null,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot put back the write record of task {JobId} after a rejected retry", jobId);
        }
    }

    /// <summary>
    /// Asks libddb whether the dataset has deferred builds (those the
    /// DroneDB build rethrow left for a retry). Any failure returns false so that a ddb
    /// availability hiccup never blocks the user-initiated retry.
    /// </summary>
    private async Task<bool> IsBuildPendingSafeAsync(string orgSlug, string dsSlug, CancellationToken ct)
    {
        try
        {
            var ds = await _datasets.GetRequiredDatasetAsync(orgSlug, dsSlug, ct: ct);
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            return ddb.IsBuildPending();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Retry: IsBuildPending check failed for {Org}/{Ds}, continuing retry", orgSlug,
                dsSlug);
            return false;
        }
    }
}
