#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Processing Platform task substrate: tool catalog, submission, history, logs and
/// artifacts for a single dataset. Combines dataset access with per-task ownership.
/// </summary>
public interface ITasksManager
{
    /// <summary>
    /// Lists the tools available for the dataset, with per-organization feature gating applied.
    /// </summary>
    /// <returns>The gated tool catalog.</returns>
    Task<TaskToolDto[]> GetToolsAsync(string orgSlug, string dsSlug, CancellationToken ct = default);

    /// <summary>
    /// Validates and enqueues a tool run, deduplicating against an equivalent in-flight task.
    /// </summary>
    /// <param name="body">Submission body: tool id, optional version/path/params and the force flag.</param>
    /// <returns>The submission outcome, including whether it was deduplicated.</returns>
    Task<SubmitTaskResponseDto> SubmitAsync(string orgSlug, string dsSlug, SubmitTaskRequestDto body,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the dataset's task history, newest first.
    /// </summary>
    /// <param name="toolId">Optional tool filter.</param>
    /// <param name="state">Optional state filter.</param>
    /// <returns>The requested page of task summaries.</returns>
    Task<TaskSummaryDto[]> ListAsync(string orgSlug, string dsSlug, string? toolId, string? state,
        int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Permanently removes concluded tasks from the dataset history and purges their artifacts.
    /// </summary>
    /// <param name="toolId">Optional tool filter.</param>
    /// <returns>The number of tasks removed.</returns>
    Task<int> ClearAsync(string orgSlug, string dsSlug, string? toolId, CancellationToken ct = default);

    /// <summary>
    /// Returns the full status of a task, including its log tail and artifact descriptor.
    /// </summary>
    /// <returns>The task status.</returns>
    Task<TaskStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default);

    /// <summary>
    /// Returns the task log lines beyond the caller's cursor.
    /// </summary>
    /// <param name="since">Cursor returned by a previous call; 0 for the whole available tail.</param>
    /// <returns>The incremental log slice.</returns>
    Task<TaskLogDto> GetLogAsync(string orgSlug, string dsSlug, string id, long since, CancellationToken ct = default);

    /// <summary>
    /// Resolves the artifact a succeeded task produced.
    /// </summary>
    /// <returns>The artifact file descriptor.</returns>
    Task<TaskArtifactFile> GetResultAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default);

    /// <summary>
    /// Cancels a task, removing it from the job store.
    /// </summary>
    /// <returns>True when the job store accepted the deletion.</returns>
    Task<bool> CancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default);

    /// <summary>
    /// Re-queues a failed task after clearing its stale prior-run state.
    /// </summary>
    /// <returns>True when the task was re-queued.</returns>
    Task<bool> RetryAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default);

    /// <summary>
    /// Removes a single concluded task from the history and purges its artifacts.
    /// </summary>
    /// <returns>True when the record was deleted.</returns>
    Task<bool> DeleteAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default);
}

/// <summary>
/// Location and transfer metadata of a task's produced artifact, rendered by the controller.
/// </summary>
/// <param name="FilePath">Absolute path of the artifact on disk.</param>
/// <param name="FileName">Download file name.</param>
/// <param name="ContentType">MIME type inferred from the file name.</param>
/// <param name="ETag">Quoted ETag derived from the artifact checksum, or null when unknown.</param>
public sealed record TaskArtifactFile(string FilePath, string FileName, string ContentType, string? ETag);
