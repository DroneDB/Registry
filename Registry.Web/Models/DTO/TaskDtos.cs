#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;

namespace Registry.Web.Models.DTO;

/// <summary>Catalog entry describing an available tool (spec §4.6 GET /tasks/tools).</summary>
public sealed record TaskToolDto(
    string Id,
    string Version,
    string Title,
    string RequiredAccess,
    bool ProducesArtifact,
    JsonElement InputSchema);

/// <summary>Body for POST /tasks.</summary>
public sealed class SubmitTaskRequestDto
{
    [JsonPropertyName("toolId")] public string ToolId { get; set; } = null!;
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    // JsonElement (System.Text.Json struct) is not bindable by Newtonsoft.Json model binding.
    // JToken? is the Newtonsoft equivalent: correctly receives any JSON value.
    [Newtonsoft.Json.JsonProperty("params")] public JToken? Params { get; set; }
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
public sealed record TaskArtifactDto(long SizeBytes, string? Sha256, string ResultUrl);

/// <summary>Single-row summary for TaskHistory (GET /tasks).</summary>
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
    string? ErrorType);

/// <summary>Full status (GET /tasks/{id}).</summary>
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
    string? Error);

/// <summary>Incremental log response (GET /tasks/{id}/log).</summary>
public sealed record TaskLogDto(long Cursor, IReadOnlyList<string> Lines, long TruncatedFromTail);
