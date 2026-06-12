#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Data.Models;

public class JobIndex
{
    [Key]
    public string JobId { get; set; } = null!; // PK (string)

    public string OrgSlug { get; set; } = null!;
    public string DsSlug { get; set; } = null!;

    public string? Hash { get; set; }
    public string? Path { get; set; }
    public string? UserId { get; set; }

    public string? Queue { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastStateChangeUtc { get; set; }

    public string CurrentState { get; set; } = "Created";

    public string? MethodDisplay { get; set; }

    public DateTime? ProcessingAtUtc { get; set; }
    public DateTime? SucceededAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime? ScheduledAtUtc { get; set; }

    // --- Processing Platform (Layer 1 - Task substrate) extensions ---

    /// <summary>Tool identifier (kebab-case). Backfilled to 'build' for legacy rows.</summary>
    public string ToolId { get; set; } = "build";

    /// <summary>Pinned tool version.</summary>
    public string ToolVersion { get; set; } = "1";

    /// <summary>Progress in 0..100, or null for indeterminate.</summary>
    public int? ProgressPercent { get; set; }

    /// <summary>Short current-phase message.</summary>
    public string? PhaseMessage { get; set; }

    /// <summary>Size of the produced artifact in bytes.</summary>
    public long? ArtifactSizeBytes { get; set; }

    /// <summary>SHA-256 of the produced artifact, for ETag and dedup.</summary>
    public string? ArtifactSha256 { get; set; }

    /// <summary>Exception type name when the task failed.</summary>
    public string? ErrorType { get; set; }

    /// <summary>Deduplication hash: sha256(toolId || version || entryHash || canonicalJson(params)).</summary>
    public string? RequestHash { get; set; }

    /// <summary>Parent job id for workflow children and continuation chains.</summary>
    public string? ParentJobId { get; set; }

    /// <summary>Workflow execution id for UI grouping.</summary>
    public string? WorkflowExecutionId { get; set; }

    /// <summary>Ring buffer JSON of the last ~100 truncated log lines.</summary>
    public string? LogTailJson { get; set; }

    /// <summary>Timestamp of the last progress update, for ETag on status.</summary>
    public DateTime? ProgressUpdatedAtUtc { get; set; }
}