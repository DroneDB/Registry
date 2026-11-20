using System;

namespace Registry.Web.Models;

public record IndexPayload(
    string OrgSlug,
    string DsSlug,
    string? Hash,
    string? UserId,
    string? Queue = null,
    string? Path = null,
    // Processing Platform (Layer 1) additions - safe defaults keep all existing call sites working.
    string ToolId = "build",
    string ToolVersion = "1",
    string? RequestHash = null,
    string? ParentJobId = null,
    string? WorkflowExecutionId = null,
    // Heavy tasks (FR-XC-25, D-013): the files the task writes (a JSON array) and the label of its target files
    string? OutputPathsJson = null,
    string? TargetLabel = null,
    // The write record the task starts with (FR-XC-27): the dataset created for it at submit, so that a rollback finds it
    // from the first moment the task exists, whatever happens to the job
    string? WriteRecordJson = null)
{
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(OrgSlug)) throw new ArgumentException("OrgSlug is required", nameof(OrgSlug));
        if (string.IsNullOrWhiteSpace(DsSlug))  throw new ArgumentException("DsSlug is required", nameof(DsSlug));
    }
}