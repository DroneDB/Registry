using System;
using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Summary of a single task for the admin dashboard. Extends the
/// per-dataset task summary with org/dataset and owner identity columns; <c>WaitingForFiles</c> and
/// <c>MissingFiles</c> as in <see cref="TaskSummaryDto"/>.
/// </summary>
public sealed record AdminTaskSummaryDto(
    string TaskId,
    string OrgSlug,
    string DsSlug,
    string ToolId,
    string Version,
    string State,
    int? ProgressPercent,
    string? PhaseMessage,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Path,
    string? UserId,
    string? Owner,
    string? ErrorType,
    DateTime? ArtifactExpiresAt,
    bool WaitingForFiles = false,
    IReadOnlyList<string>? MissingFiles = null);

/// <summary>Paged result of the admin task list.</summary>
public sealed record AdminTaskListDto(
    IReadOnlyList<AdminTaskSummaryDto> Items,
    long Total,
    int Skip,
    int Take);
