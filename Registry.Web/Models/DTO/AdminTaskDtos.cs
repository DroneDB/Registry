#nullable enable
using System;
using System.Collections.Generic;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Summary of a single task for the admin dashboard (spec §B.1.3). Extends the
/// per-dataset task summary with org/dataset and owner identity columns.
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
    DateTime? ArtifactExpiresAt);

/// <summary>Paged result of the admin task list (spec §B.1.3).</summary>
public sealed record AdminTaskListDto(
    IReadOnlyList<AdminTaskSummaryDto> Items,
    long Total,
    int Skip,
    int Take);
