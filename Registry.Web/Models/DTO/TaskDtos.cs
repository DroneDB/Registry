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
/// the task.
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
    DateTime? ArtifactExpiresAt,
    bool Background = false,
    bool WaitingForFiles = false,
    IReadOnlyList<string>? MissingFiles = null);

/// <summary>Full status (GET /tasks/{id}); <c>WaitingForFiles</c> and <c>MissingFiles</c> as in <see cref="TaskSummaryDto"/>.</summary>
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
    IReadOnlyList<string>? MissingFiles = null);

/// <summary>Incremental log response (GET /tasks/{id}/log).</summary>
public sealed record TaskLogDto(long Cursor, IReadOnlyList<string> Lines, long TruncatedFromTail);
