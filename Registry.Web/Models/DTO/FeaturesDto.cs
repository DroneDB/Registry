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
    /// When true, anonymous (not logged-in) users cannot download bulk archives
    /// (whole dataset, folders, or multi-file selections). Single-file downloads are always allowed.
    /// Clients use this flag to disable bulk-download UI controls for anonymous users.
    /// </summary>
    public bool DisableAnonymousBulkDownloads { get; set; }

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
    /// Size threshold (bytes) above which an authenticated bulk download is offloaded to
    /// the asynchronous <c>bulk-download</c> task. Whole-dataset downloads always use the
    /// async path. Clients compare the selection size against this value to route the download.
    /// </summary>
    public long BulkDownloadAsyncThresholdBytes { get; set; }

    /// <summary>
    /// Authoritative catalog of available task tools (id, title, result extension,
    /// artifact flag). Clients use this as the single source of truth for task tool
    /// metadata instead of hardcoded maps.
    /// </summary>
    public TaskToolInfoDto[]? TaskTools { get; set; }

    /// <summary>
    /// Authoritative task state machine (value + terminal flag). Clients derive the
    /// active/terminal classification and the state filter options from this list.
    /// </summary>
    public TaskStateInfoDto[]? TaskStates { get; set; }

    /// <summary>
    /// The entry types libddb knows and what it does with each (<c>DDBGetEntryTypes</c>, passed through in libddb's
    /// order; Registry has no type rules of its own). Clients derive which entries build, which have a thumbnail and
    /// which extensions are sidecars from this list instead of hardcoding them.
    /// </summary>
    public EntryTypeDto[]? EntryTypes { get; set; }

    /// <summary>
    /// The archive file name suffixes Registry's own extraction accepts: lowercase, without the leading dot,
    /// compound ones (<c>tar.gz</c>) before their own suffixes (<c>gz</c>). A name that ends with one can be
    /// extracted into a dataset.
    /// </summary>
    public string[]? ArchiveExtensions { get; set; }

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

/// <summary>
/// Authoritative descriptor of an available task tool, surfaced to clients via the
/// features payload so the UI no longer hardcodes tool titles/extensions.
/// The gating fields (<paramref name="Hidden"/>, <paramref name="Disabled"/>,
/// <paramref name="DisabledMessage"/>) are computed server-side for the current caller.
/// </summary>
public sealed record TaskToolInfoDto(
    string Id,
    string Version,
    string Title,
    string RequiredAccess,
    bool ProducesArtifact,
    string? ResultExtension,
    bool Hidden,
    bool Disabled,
    string? DisabledMessage);

/// <summary>
/// An entry type as libddb describes it (<c>DDBGetEntryTypes</c>).
/// </summary>
/// <param name="Id">libddb's numeric id of the type (the <c>type</c> of an entry).</param>
/// <param name="Name">Its name, as <c>DDBRescan</c> takes it.</param>
/// <param name="Buildable">libddb builds entries of the type.</param>
/// <param name="Thumbnail"><c>none</c>, <c>file</c> (rendered from the entry's file) or <c>build</c> (from an artifact once built).</param>
/// <param name="SidecarExtensions">Lowercase extensions, without the dot, of the files that belong to a main file of the type.</param>
/// <param name="Roles">The artifact roles a build of the type can produce.</param>
public sealed record EntryTypeDto(
    int Id,
    string Name,
    bool Buildable,
    string Thumbnail,
    string[] SidecarExtensions,
    string[] Roles);

/// <summary>
/// Authoritative descriptor of a task state (value + terminal flag), surfaced to
/// clients so the UI derives the state machine from the server.
/// </summary>
public sealed record TaskStateInfoDto(string Value, bool Terminal);
