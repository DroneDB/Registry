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
    private readonly ILogger<TaskRetry> _logger;
    private readonly IHeavyTaskRunner? _runner;
    private readonly IHttpContextAccessor? _http;

    public TaskRetry(TaskAuthorizer authorizer, IBackgroundJobsProcessor processor, IJobIndexWriter writer,
        IDatasetResolver datasets, IDdbManager ddbManager, ILogger<TaskRetry> logger,
        IHeavyTaskRunner? runner = null, IHttpContextAccessor? http = null)
    {
        _authorizer = authorizer;
        _processor = processor;
        _writer = writer;
        _datasets = datasets;
        _ddbManager = ddbManager;
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

        // A heavy task runs the submit checks of its tool first, with the parameters it was submitted with: its
        // validation, an output that another active task writes (409), the quota of its owner (507). The re-queue
        // runs under the submit lock, once every check passed.
        var stored = _runner is null ? null : _processor.StoredHeavyRequest(job.JobId);
        if (stored is not null)
        {
            var request = new HeavyTaskSubmitRequest(orgSlug, dsSlug, stored.ToolId, stored.Version, stored.Path,
                stored.Params, Force: true, job.UserId, _http?.HttpContext?.User, job.Hash, job.ParentJobId,
                job.WorkflowExecutionId);
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
        if (_processor.Requeue(jobId))
        {
            await _writer.ResetForRequeueAsync(jobId, ct);
            return;
        }

        _logger.LogInformation(
            "Retry of task {JobId} rejected: row state '{State}' (Failed-only guard or job missing in Hangfire)",
            jobId, rowState);
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
