#nullable enable
using Registry.Web.Models.Configuration;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Represents the status of all platform feature flags.
/// </summary>
public class FeaturesDto
{
    /// <summary>
    /// Whether organization-level member management is enabled.
    /// </summary>
    public bool OrganizationMemberManagement { get; set; }

    /// <summary>
    /// Whether local user management is enabled (false when external auth is configured).
    /// </summary>
    public bool UserManagement { get; set; }

    /// <summary>
    /// Whether the per-user storage limiter is enabled.
    /// </summary>
    public bool StorageLimiter { get; set; }

    /// <summary>
    /// Maximum number of concurrent downloads per user. Null = unlimited (feature disabled).
    /// </summary>
    public int? MaxConcurrentDownloadsPerUser { get; set; }

    /// <summary>
    /// Password complexity policy. Null when no policy is enforced.
    /// </summary>
    public PasswordPolicyDto? PasswordPolicy { get; set; }

    /// <summary>
    /// File names (in dataset root) considered as dataset thumbnail candidates.
    /// </summary>
    public string[]? DatasetThumbnailCandidates { get; set; }

    /// <summary>
    /// Maximum allowed output size in bytes for GeoTIFF raster export. Null = unlimited.
    /// Clients use this to disable the export button when the estimated output exceeds the limit.
    /// </summary>
    public long? MaxExportSizeBytes { get; set; }

    /// <summary>
    /// Hub UI branding and customization options. Materializes <c>window.HubOptions</c>
    /// on the client. Always populated from <c>AppSettings:HubOptions</c> in production
    /// (defaults shipped via <c>appsettings-default.json</c>); only null if an admin has
    /// explicitly removed the section from <c>appsettings.json</c>.
    /// </summary>
    public HubOptions? HubOptions { get; set; }

    /// <summary>
    /// Semver of the Hub (Vue ClientApp) currently extracted in <c>{StoragePath}/ClientApp/</c>.
    /// The SPA compares this with its own build-time <c>__HUB_VERSION__</c> on boot
    /// to detect a server-side upgrade/downgrade and force a hard reload.
    /// </summary>
    public string? HubVersion { get; set; }

    /// <summary>
    /// Registry assembly version. Surfaced for the post-update notice dialog.
    /// </summary>
    public string? RegistryVersion { get; set; }

    /// <summary>
    /// Native DroneDB library version. Surfaced for the post-update notice dialog.
    /// </summary>
    public string? DdbVersion { get; set; }
}
