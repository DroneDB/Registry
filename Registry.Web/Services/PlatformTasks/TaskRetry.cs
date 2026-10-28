#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Managers;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
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
    private readonly ILogger<TaskRetry> _logger;

    public TaskRetry(TaskAuthorizer authorizer, IBackgroundJobsProcessor processor, IJobIndexWriter writer,
        IDatasetResolver datasets, IDdbManager ddbManager, ILogger<TaskRetry> logger)
    {
        _authorizer = authorizer;
        _processor = processor;
        _writer = writer;
        _datasets = datasets;
        _ddbManager = ddbManager;
        _logger = logger;
    }

    /// <summary>Re-queues a failed task; false when the job store did not accept it (see the conflict it throws).</summary>
    /// <exception cref="ConflictException">The task cannot be retried in its state, or the pending-build sweep owns the retry.</exception>
    public async Task<bool> RetryAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Write, ct);

        // Sweep-ownership rule: dependency-gated builds are re-run by the pending-build sweep;
        // the Retry button must not start a competing run while libddb reports a deferred build.
        if (job.ToolId == HeavyToolIds.Build && await IsBuildPendingSafeAsync(orgSlug, dsSlug, ct))
            throw new ConflictException(
                "A pending build retry is already scheduled for this dataset; the pending-build sweep owns that retry");

        // Re-queue is gated by Hangfire's LIVE state (Failed-only) - authoritative, since it
        // reads the actual store rather than the lagging display-only JobIndex row. Wipe stale
        // prior-run state (error fields, timestamps, artifacts) only AFTER an accepted transition,
        // so a rejected retry (job purged/expired or state moved on) never destroys the still
        // valid failure diagnostics the user just asked about.
        var requeued = _processor.Requeue(job.JobId);
        if (requeued)
        {
            await _writer.ResetForRequeueAsync(job.JobId, ct);
            return true;
        }

        _logger.LogInformation(
            "Retry of task {JobId} rejected: row state '{State}' (Failed-only guard or job missing in Hangfire)",
            job.JobId, job.CurrentState);
        // State-free message on purpose: the DB row state can lag the guard's live read,
        // so do not assert a state here; details are in the log line above.
        throw new ConflictException(
            "Task cannot be retried in its current state or no longer exists in the job store");
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
            // Without safe the lookup throws when the dataset is missing: never null
            var ds = (await _datasets.GetDatasetAsync(orgSlug, dsSlug, ct: ct))!;
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
