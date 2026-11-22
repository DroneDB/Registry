using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Managers;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>Ending a task and clearing the history (out of <c>TasksManager</c>).</summary>
public sealed class TaskLifecycle
{
    private readonly TaskAuthorizer _authorizer;
    private readonly IJobIndexWriter _writer;
    private readonly TaskArtifactStore _artifacts;
    private readonly TaskCanceller _canceller;
    private readonly ILogger<TaskLifecycle> _logger;

    /// <summary>The answer to the cancel of a task whose write record is committed (D-002, FR-XC-27).</summary>
    public const string CommittedMessage =
        "This task can no longer be cancelled: it has already published its result";

    public TaskLifecycle(TaskAuthorizer authorizer, IJobIndexWriter writer, TaskArtifactStore artifacts,
        TaskCanceller canceller, ILogger<TaskLifecycle> logger)
    {
        _authorizer = authorizer;
        _writer = writer;
        _artifacts = artifacts;
        _canceller = canceller;
        _logger = logger;
    }

    /// <summary>Permanently removes the dataset's concluded tasks (optionally of one tool) and purges their artifacts; the number removed.</summary>
    public async Task<int> ClearAsync(string orgSlug, string dsSlug, string? toolId, CancellationToken ct = default)
    {
        await _authorizer.ResolveDatasetAsync(orgSlug, dsSlug, AccessType.Write, ct);

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
    /// Cancels a queued or running task through <see cref="TaskCanceller"/>: deletes its job from the job store. A task
    /// whose job the store no longer has (lost when an InMemory store restarted) can never run, so it ends here too
    /// (Deleted); false only when the task ended meanwhile. A task that adds dataset files is rolled back from its write
    /// record: the cancel request is recorded first, so the task can no longer commit. A reader cancels
    /// the tasks it started of a tool that needs only read access (<see cref="TaskAuthorizer.LoadToCancelAsync"/>).
    /// </summary>
    /// <exception cref="BadRequestException">The task has already ended, or committed its result.</exception>
    public async Task<bool> CancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await _authorizer.LoadToCancelAsync(orgSlug, dsSlug, id, ct);
        if (TaskStateCatalog.IsTerminal(job.CurrentState))
            throw new BadRequestException(
                $"The task has already ended ({job.CurrentState}); only a queued or running task can be cancelled",
                noRetry: true);

        // Before its job is deleted: from here on the task cannot commit, and a task that committed is not cancelled
        if (await _canceller.RequestAsync(job.JobId, ct) == TaskCancelRequestResult.Committed)
            throw new BadRequestException(CommittedMessage, noRetry: true);

        // Who cancelled, before the job ends: the task is then labelled "Cancelled", not "Deleted" (D-038). The
        // other writers of Deleted (job sync, recoveries, dataset clean-up) never set it
        var userId = await _authorizer.CurrentUserIdAsync();
        var marked = userId is not null && await _writer.SetCancelledByAsync(job.JobId, userId, ct);

        var ended = await _canceller.EndAsync(job.JobId, orgSlug, dsSlug, ct);

        // The task ended by itself meanwhile: nobody cancelled it
        if (!ended && marked)
            await _writer.SetCancelledByAsync(job.JobId, null, ct);
        return ended;
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
