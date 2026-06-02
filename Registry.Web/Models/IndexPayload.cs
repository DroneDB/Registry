#nullable enable
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
    string? WorkflowExecutionId = null)
{
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(OrgSlug)) throw new ArgumentException("OrgSlug is required", nameof(OrgSlug));
        if (string.IsNullOrWhiteSpace(DsSlug))  throw new ArgumentException("DsSlug is required", nameof(DsSlug));
    }
}