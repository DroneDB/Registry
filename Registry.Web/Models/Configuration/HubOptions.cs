using Newtonsoft.Json;

namespace Registry.Web.Models.Configuration;

/// <summary>
/// User-customizable Hub branding and UI options.
/// All values are surfaced to the Vue frontend through the
/// <see cref="Models.DTO.FeaturesDto"/> (<c>GET /sys/features</c>) and exposed at
/// runtime as <c>window.HubOptions</c>.
/// </summary>
/// <remarks>
/// Null properties are omitted from the JSON payload to preserve the
/// <c>HubOptions.X !== undefined</c> fallback semantics expected by the frontend.
/// </remarks>
public class HubOptions
{
    /// <summary>
    /// URL of the navbar logo. Use a path under <c>/branding/...</c> to point to a
    /// file dropped in <c>{StoragePath}/branding/</c>, or any absolute URL.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string AppLogo { get; set; }

    /// <summary>
    /// Display name used as the document title and as fallback when no logo is set.
    /// When null, the frontend defaults to <c>DroneDB</c>.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string AppName { get; set; }

    /// <summary>
    /// Optional CSS icon class shown next to the app name when no logo is configured.
    /// When null, the frontend defaults to <c>icon-dronedb</c>.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string AppIcon { get; set; }

    /// <summary>
    /// Whether to show the registration link on the login page. Default is <c>true</c>.
    /// </summary>
    public bool ShowRegistrationLink { get; set; } = true;

    /// <summary>
    /// Hides every dataset creation entry-point in the UI when <c>true</c>.
    /// </summary>
    public bool DisableDatasetCreation { get; set; }

    /// <summary>
    /// Hides storage usage info in the header when <c>true</c>.
    /// </summary>
    public bool DisableStorageInfo { get; set; }

    /// <summary>
    /// Hides account-management entry-points in the UI when <c>true</c>.
    /// </summary>
    public bool DisableAccountManagement { get; set; }

    /// <summary>
    /// When set, the UI is locked to a single organization slug
    /// (typical of self-hosted single-tenant deployments).
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string SingleOrganization { get; set; }

    /// <summary>
    /// Hides organization-creation/edit entry-points when <c>true</c>.
    /// </summary>
    public bool ReadOnlyOrgs { get; set; }

    /// <summary>
    /// Optional favicon / web-manifest configuration. When null, the Hub falls
    /// back to whatever default favicon ships with the embedded ClientApp.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public FaviconOptions Favicon { get; set; }
}
