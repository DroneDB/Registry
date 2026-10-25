namespace Registry.Web;

// This class is the definition of a bad idea
public static class MagicStrings
{
    public const string PublicOrganizationSlug = "public";
    public const string DefaultDatasetSlug = "default";
    public const string AnonymousUserName = "anonymous";
    public const string MaxStorageKey = "maxStorageMB";

    public const string TileCacheSeed = "tile";
    public const string ThumbnailCacheSeed = "thumb";
    public const string BuildPendingTrackerCacheSeed = "build-pending-tracker";
    public const string TasksListCacheSeed = "tasks-list";
    public const string DatasetVisibilityCacheSeed = "dataset-visibility";
    public const string DatasetNameCacheSeed = "dataset-name";
    public const string OrganizationsListCacheSeed = "organizations-list";
    public const string DatasetsListCacheSeed = "datasets-list";
    public const string OgcRenderCacheSeed = "ogc-render";

    public const string AutoBuildServiceUserId = "auto-build-service";

    public const string IdentityConnectionName = "IdentityConnection";
    public const string RegistryConnectionName = "RegistryConnection";
    public const string HangfireConnectionName = "HangfireConnection";

    public const string HangFireUrl = "/hangfire";
    public const string SwaggerUrl = "/swagger";
    public const string ScalarUrl = "/scalar/v1";
    public const string QuickHealthUrl = "/quickhealth";
    public const string HealthUrl = "/health";

    /// <summary>
    /// Anonymous liveness probe (container HEALTHCHECK): the process serves HTTP, nothing more.
    /// Mapped by every HTTP host (web server and thumbnail generator), see <c>LivenessEndpoint</c>.
    /// </summary>
    public const string LivenessUrl = "/health/live";
    public const string VersionUrl = "/version";

    public const string DefaultHost = "localhost";
    public const string SpaRoot = "ClientApp";
    public const string DdbArchive = "ddb";
    public const string AppSettingsFileName = "appsettings.json";
    public const string AppSettingsDefaultFileName = "appsettings-default.json";

    /// <summary>The identity of the deployment, created at the first start (JSON, in the storage folder all nodes share).</summary>
    public const string InstanceIdFileName = ".instance-id";

    /// <summary>The public URL a request was served on, for the update check when no external URL is configured.</summary>
    public const string ObservedBaseUrlFileName = ".observed-base-url";

    /// <summary>What an administrator declared about the use of the deployment (the Hub's "Usage and contact").</summary>
    public const string UpdateCheckSettingsFileName = ".update-check-settings.json";

    /// <summary>The last update check result, the time of the last failure and the last version e-mailed about.</summary>
    public const string UpdateCheckCacheFileName = ".update-check-cache.json";

    /// <summary>
    /// Marker file dropped inside the embedded Hub archive (and therefore in the
    /// extracted folder) carrying the Hub semantic version. Used by Registry to
    /// decide whether the on-disk Hub needs to be re-extracted after an upgrade.
    /// </summary>
    public const string HubVersionFile = "version.txt";

    /// <summary>
    /// Folder under <c>StoragePath</c> hosting user-supplied branding assets
    /// (logos, favicons, manifest). Preserved across Hub upgrades.
    /// </summary>
    public const string BrandingFolder = "branding";

    /// <summary>
    /// URL prefix at which the branding folder is served.
    /// </summary>
    public const string BrandingUrlPrefix = "/branding";

    public const string DdbReleasesPageUrl = "https://github.com/DroneDB/DroneDB/releases";
    public const string DdbInstallPageUrl = "https://docs.dronedb.app/download.html";
}