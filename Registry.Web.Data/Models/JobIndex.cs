#nullable enable
using System;
using System.ComponentModel.DataAnnotations;

namespace Registry.Web.Data.Models;

/// <summary>
/// EF entity for Hangfire job index tracking (state, timestamps, tool info).
/// </summary>
public class JobIndex
{
    /// <summary>Hangfire job identifier (primary key).</summary>
    [Key]
    public string JobId { get; set; } = null!; // PK (string)

    /// <summary>Organization slug this job belongs to.</summary>
    public string OrgSlug { get; set; } = null!;

    /// <summary>Dataset slug this job belongs to.</summary>
    public string DsSlug { get; set; } = null!;

    /// <summary>Content hash of the entry being processed.</summary>
    public string? Hash { get; set; }

    /// <summary>File path within the dataset.</summary>
    public string? Path { get; set; }

    /// <summary>User ID of the user who triggered the job.</summary>
    public string? UserId { get; set; }

    /// <summary>Hangfire queue name.</summary>
    public string? Queue { get; set; }

    /// <summary>Job creation timestamp (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Timestamp of the last state transition (UTC).</summary>
    public DateTime? LastStateChangeUtc { get; set; }

    /// <summary>Current Hangfire state (Created, Processing, Succeeded, Failed, Deleted).</summary>
    public string CurrentState { get; set; } = "Created";

    /// <summary>Human-readable method display name for the job.</summary>
    public string? MethodDisplay { get; set; }

    /// <summary>Timestamp when the job started processing (UTC).</summary>
    public DateTime? ProcessingAtUtc { get; set; }

    /// <summary>Timestamp when the job succeeded (UTC).</summary>
    public DateTime? SucceededAtUtc { get; set; }

    /// <summary>Timestamp when the job failed (UTC).</summary>
    public DateTime? FailedAtUtc { get; set; }

    /// <summary>Timestamp when the job was deleted (UTC).</summary>
    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>Timestamp when the job was scheduled (UTC).</summary>
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