using System;

namespace Registry.Web.Models.DTO;

/// <summary>
/// What the update check knows (<c>GET /sys/update-info</c>, and the answer of <c>PUT /sys/update-check/settings</c>).
/// The fields that come from the update check service are null until a check succeeded.
/// </summary>
public sealed class UpdateInfoDto
{
    /// <summary>The identity of this deployment: the value to quote in a request to delete the data held about it.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The installed Registry version.</summary>
    public string? CurrentVersion { get; set; }

    /// <summary>The latest published Registry version.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>Whether <see cref="LatestVersion"/> is newer than <see cref="CurrentVersion"/>. False when either is not a version.</summary>
    public bool IsUpdateAvailable { get; set; }

    /// <summary>Whether a release between the installed version and the latest has a security section in its notes.</summary>
    public bool IsSecurityUpdate { get; set; }

    /// <summary>When <see cref="LatestVersion"/> was published (<c>YYYY-MM-DD</c>).</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>The page of the latest release.</summary>
    public string? ChangelogUrl { get; set; }

    /// <summary>The page that compares the installed version with the latest.</summary>
    public string? CompareUrl { get; set; }

    /// <summary>
    /// The notes of every release between the installed version and the latest, merged into one Markdown document. Untrusted
    /// text from the update check service: render it sanitized. Empty when no update is available.
    /// </summary>
    public string? ReleaseNotes { get; set; }

    /// <summary>
    /// The licence request form, with this deployment's <c>instance</c>, <c>url</c> and <c>version</c> in the query. Always set.
    /// </summary>
    public string LicenseRequestUrl { get; set; } = string.Empty;

    /// <summary>How the administrator declared the deployment is used.</summary>
    public string? UseDeclaration { get; set; }

    /// <summary>The address the administrator agreed to be contacted on.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>When the last check that succeeded received its answer (UTC).</summary>
    public DateTime? LastChecked { get; set; }
}

/// <summary>The body of <c>PUT /sys/update-check/settings</c>.</summary>
public sealed class UpdateCheckSettingsDto
{
    /// <summary>
    /// How the deployment is used: <c>personal</c>, <c>nonprofit</c>, <c>evaluation</c>, <c>commercial_licensed</c> or
    /// <c>commercial_needs_license</c>; empty or null for not stated.
    /// </summary>
    public string? UseDeclaration { get; set; }

    /// <summary>The address to send security and release news to; empty or null removes it.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>The administrator agrees to be contacted on <see cref="ContactEmail"/>. Required when an address is given.</summary>
    public bool ContactConsent { get; set; }
}
