#nullable enable
using System.Collections.Generic;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// Availability state an administrator assigns to a heavy tool via feature gating.
/// </summary>
public enum HeavyToolAvailability
{
    /// <summary>
    /// Fully available (still subject to the role and organization allowlists).
    /// This is the default for tools that are not configured.
    /// </summary>
    Enabled,

    /// <summary>
    /// Visible but not usable: the tool is rendered greyed out in the UI with
    /// <see cref="HeavyToolConfig.DisabledMessage"/> as a tooltip, and a submit is
    /// rejected with HTTP 403 and the same message.
    /// </summary>
    Disabled,

    /// <summary>
    /// Not shown in the UI at all. A submit is rejected with HTTP 403.
    /// </summary>
    Hidden
}

/// <summary>
/// Feature gating configuration for a single heavy tool. Bound from
/// <c>AppSettings:ProcessingPlatform:Tools:{toolId}</c>.
/// </summary>
public class HeavyToolConfig
{
    /// <summary>
    /// Tool availability state. Default <see cref="HeavyToolAvailability.Enabled"/>.
    /// </summary>
    public HeavyToolAvailability Availability { get; set; } = HeavyToolAvailability.Enabled;

    /// <summary>
    /// Message shown as a tooltip when the tool is rendered disabled (greyed out) and
    /// used as the body of the 403 on a blocked submit. Ignored when the tool is Hidden.
    /// </summary>
    public string? DisabledMessage { get; set; }

    /// <summary>
    /// Roles allowed to use the tool. Empty list = all authenticated users.
    /// The value <c>"admin"</c> maps to the reserved system administrator role.
    /// </summary>
    public List<string> AllowedRoles { get; set; } = [];

    /// <summary>
    /// Organization slugs allowed to use the tool. Empty list = all organizations.
    /// Same allowlist semantics as <see cref="AllowedRoles"/>: when non-empty, the
    /// current organization must be listed or the tool is treated as not available.
    /// </summary>
    public List<string> AllowedOrgs { get; set; } = [];

    /// <summary>
    /// Controls what happens when the caller fails the role or organization allowlist.
    /// <c>true</c> (default) = the tool is Hidden; <c>false</c> = the tool is Disabled
    /// (greyed out with <see cref="DisabledMessage"/>).
    /// </summary>
    public bool HideWhenNotAllowed { get; set; } = true;

    /// <summary>
    /// Per-tool cap on concurrent tasks per user. 0 = inherit the global
    /// <c>MaxConcurrentTasksPerUser</c>.
    /// </summary>
    public int MaxConcurrentPerUser { get; set; } = 0;

    /// <summary>
    /// Per-tool cap on queued tasks per user. 0 = inherit the global
    /// <c>MaxQueuedTasksPerUser</c>.
    /// </summary>
    public int MaxQueuedPerUser { get; set; } = 0;
}
