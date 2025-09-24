#nullable enable
using System;

namespace Registry.Web.Models;

public record IndexPayload(string OrgSlug, string DsSlug, string? Path, string? UserId, string? Queue = null)
{
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(OrgSlug)) throw new ArgumentException("OrgSlug is required", nameof(OrgSlug));
        if (string.IsNullOrWhiteSpace(DsSlug))  throw new ArgumentException("DsSlug is required", nameof(DsSlug));
    }
}