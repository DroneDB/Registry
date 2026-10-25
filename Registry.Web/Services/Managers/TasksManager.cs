#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Processing Platform task substrate for a single dataset (<see cref="ITasksManager"/>, what the controller calls)
/// over the services it is split into: the tool catalog and submission, the history
/// (list, status, log, result), ending a task, retrying one, and the change subscription. Authorization combines
/// dataset access with per-task ownership (<see cref="TaskAuthorizer"/>).
/// </summary>
public sealed class TasksManager : ITasksManager
{
    private readonly TaskSubmission _submission;
    private readonly TaskHistory _history;
    private readonly TaskLifecycle _lifecycle;
    private readonly TaskRetry _retry;
    private readonly IDatasetAccessResolver _access;
    private readonly ITaskChangeNotifier? _changes;

    public TasksManager(TaskSubmission submission, TaskHistory history, TaskLifecycle lifecycle, TaskRetry retry,
        IDatasetAccessResolver access, ITaskChangeNotifier? changes = null)
    {
        _submission = submission;
        _history = history;
        _lifecycle = lifecycle;
        _retry = retry;
        _access = access;
        _changes = changes;
    }

    public Task<TaskToolDto[]> GetToolsAsync(string orgSlug, string dsSlug, CancellationToken ct = default) =>
        _submission.GetToolsAsync(orgSlug, dsSlug, ct);

    public Task<SubmitTaskResponseDto> SubmitAsync(string orgSlug, string dsSlug, SubmitTaskRequestDto body,
        CancellationToken ct = default) => _submission.SubmitAsync(orgSlug, dsSlug, body, ct);

    public Task<TaskListPage> ListPageAsync(string orgSlug, string dsSlug, string? toolId, string? state,
        int skip, int take, CancellationToken ct = default) =>
        _history.ListPageAsync(orgSlug, dsSlug, toolId, state, skip, take, ct);

    public async Task<IDisposable> SubscribeAsync(string orgSlug, string dsSlug, Action onChange)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read);

        return _changes?.Subscribe(orgSlug, dsSlug, onChange)
               ?? throw new InvalidOperationException("Task change notifications are not configured");
    }

    public Task<int> ClearAsync(string orgSlug, string dsSlug, string? toolId, CancellationToken ct = default) =>
        _lifecycle.ClearAsync(orgSlug, dsSlug, toolId, ct);

    public Task<TaskStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default) =>
        _history.GetStatusAsync(orgSlug, dsSlug, id, ct);

    public Task<TaskLogDto> GetLogAsync(string orgSlug, string dsSlug, string id, long since, CancellationToken ct = default) =>
        _history.GetLogAsync(orgSlug, dsSlug, id, since, ct);

    public Task<TaskArtifactFile> GetResultAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default) =>
        _history.GetResultAsync(orgSlug, dsSlug, id, ct);

    public Task<bool> CancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default) =>
        _lifecycle.CancelAsync(orgSlug, dsSlug, id, ct);

    public Task<bool> RetryAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default) =>
        _retry.RetryAsync(orgSlug, dsSlug, id, ct);

    public Task<bool> DeleteAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default) =>
        _lifecycle.DeleteAsync(orgSlug, dsSlug, id, ct);
}
