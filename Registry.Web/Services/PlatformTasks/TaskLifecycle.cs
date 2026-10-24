#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Managers;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>Ending a task and clearing the history (RQ-12, out of <c>TasksManager</c>).</summary>
public interface ITaskLifecycle
{
    /// <summary>Permanently removes the dataset's concluded tasks (optionally of one tool) and purges their artifacts; the number removed.</summary>
    Task<int> ClearAsync(string orgSlug, string dsSlug, string? toolId, CancellationToken ct = default);

    /// <summary>Cancels (deletes from the job store) a task; false when the job store no longer has it.</summary>
    Task<bool> CancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default);

    /// <summary>Removes one concluded task from the history and purges its artifacts.</summary>
    /// <exception cref="BadRequestException">The task has not concluded.</exception>
    Task<bool> DeleteAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default);
}

/// <summary>Default <see cref="ITaskLifecycle"/>.</summary>
public sealed class TaskLifecycle : ITaskLifecycle
{
    private readonly IDatasetAccessResolver _access;
    private readonly ITaskAuthorizer _authorizer;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly IJobIndexWriter _writer;
    private readonly ITaskArtifactStore _artifacts;
    private readonly ILogger<TaskLifecycle> _logger;

    public TaskLifecycle(IDatasetAccessResolver access, ITaskAuthorizer authorizer, IBackgroundJobsProcessor processor,
        IJobIndexWriter writer, ITaskArtifactStore artifacts, ILogger<TaskLifecycle> logger)
    {
        _access = access;
        _authorizer = authorizer;
        _processor = processor;
        _writer = writer;
        _artifacts = artifacts;
        _logger = logger;
    }

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

    public async Task<bool> CancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Write, ct);
        return _processor.Delete(job.JobId);
    }

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
