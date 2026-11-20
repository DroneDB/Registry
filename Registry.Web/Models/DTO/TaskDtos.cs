using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

/// <summary>Catalog entry describing an available tool (GET /tasks/tools).</summary>
/// <remarks><c>InputSchema</c> is the tool's JSON Schema, written as the document it is.</remarks>
public sealed record TaskToolDto(
    string Id,
    string Version,
    string Title,
    string RequiredAccess,
    bool ProducesArtifact,
    JsonElement InputSchema,
    bool Hidden,
    bool Disabled,
    string? DisabledMessage);

/// <summary>Body for POST /tasks.</summary>
public sealed class SubmitTaskRequestDto
{
    [JsonPropertyName("toolId")] public string ToolId { get; set; } = null!;
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("params")] public JsonElement? Params { get; set; }
    [JsonPropertyName("force")] public bool Force { get; set; }
}

/// <summary>202 response for POST /tasks.</summary>
public sealed record SubmitTaskResponseDto(
    string TaskId,
    string ToolId,
    string Version,
    bool Deduplicated,
    string StatusUrl,
    string ResultUrl,
    long? EstimatedOutputBytes);

/// <summary>Progress sub-object of a task status.</summary>
public sealed record TaskProgressDto(int? Percent, string? Phase, string? Message);

/// <summary>Artifact descriptor of a completed task.</summary>
public sealed record TaskArtifactDto(long SizeBytes, string? Sha256, string ResultUrl, DateTime? ExpiresAt);

/// <summary>
/// Single-row summary for TaskHistory (GET /tasks). <c>Background</c> is set for the rebuild of an entry made with an
/// older layout, which runs when its queue has a free worker, not when somebody asked for it.
/// <c>WaitingForFiles</c> is set for a succeeded build whose entry waits now for the files it names
/// (<c>MissingFiles</c>, dataset-relative; null otherwise): libddb defers such a build without failing it, and it
/// runs by itself when one of those files is added. It is read from the entry when the list is made, not kept with
/// the task. <c>Error</c> is why a failed task failed, as any reader of the task may see it (no server path;
/// <c>JobLogText.UserMessage</c>), next to <c>ErrorType</c>, the exception type name; null when the task did not fail
/// or failed before the message was recorded. <c>Cancellable</c> is whether a cancel is accepted: false for an ended task
/// and for one that committed its result (FR-XC-27, D-002: its cancel answers 400).
/// <c>TargetLabel</c> names the task's target files (the tool's label, FR-XC-25), or null. <c>Actions</c> are what the
/// caller may do with the task (<see cref="Services.PlatformTasks.TaskActions"/>: <c>cancel</c>, <c>retry</c>,
/// <c>delete</c>, <c>viewLog</c>; D-024). <c>WaitingForNode</c> is set while a processing node holds the task in its
/// queue. <c>CancelledBy</c> is the name of the user who cancelled a <c>Deleted</c> task, or null (D-038).
/// </summary>
public sealed record TaskSummaryDto(
    string TaskId,
    string ToolId,
    string Version,
    string State,
    int? ProgressPercent,
    string? PhaseMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Path,
    string? ParentJobId,
    string? WorkflowExecutionId,
    string? ErrorType,
    string? Error,
    DateTime? ArtifactExpiresAt,
    bool Background = false,
    bool WaitingForFiles = false,
    IReadOnlyList<string>? MissingFiles = null,
    bool Cancellable = false,
    string? TargetLabel = null,
    IReadOnlyList<string>? Actions = null,
    bool WaitingForNode = false,
    string? CancelledBy = null);

/// <summary>
/// Full status (GET /tasks/{id}); <c>Error</c>, <c>WaitingForFiles</c>, <c>MissingFiles</c>, <c>TargetLabel</c>,
/// <c>Actions</c>, <c>WaitingForNode</c> and <c>CancelledBy</c> as in
/// <see cref="TaskSummaryDto"/> (<c>Error</c> falls back to the exception type name of a task that failed before the
/// message was recorded).
/// </summary>
public sealed record TaskStatusDto(
    string TaskId,
    string ToolId,
    string Version,
    string State,
    TaskProgressDto Progress,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? ParentJobId,
    string? WorkflowExecutionId,
    long LogCursor,
    IReadOnlyList<string> LogTail,
    TaskArtifactDto? Artifact,
    string? Error,
    bool WaitingForFiles = false,
    IReadOnlyList<string>? MissingFiles = null,
    string? TargetLabel = null,
    IReadOnlyList<string>? Actions = null,
    bool WaitingForNode = false,
    string? CancelledBy = null);

/// <summary>Incremental log response (GET /tasks/{id}/log).</summary>
public sealed record TaskLogDto(long Cursor, IReadOnlyList<string> Lines, long TruncatedFromTail);
