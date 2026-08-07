using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Builds the ConfigurationDataDto from AppSettings + embedded defaults.
/// Separated from the controller to keep Single Responsibility.
/// </summary>
public class ConfigurationDataBuilder : IConfigurationDataBuilder
{
    private readonly AppSettings _settings;
    private readonly AppSettings _defaults;
    private readonly IConfiguration _configuration;

    public ConfigurationDataBuilder(IOptions<AppSettings> settings, IConfiguration configuration)
    {
        _settings = settings.Value;
        _configuration = configuration;
        _defaults = LoadDefaults();
    }

    public ConfigurationDataDto Build()
    {
        var sections = new List<ConfigurationSectionDataDto>();

        // 1. Auth & Security
        sections.Add(BuildAuthSection());

        // 2. Default Admin
        sections.Add(BuildDefaultAdminSection());

        // 3. Providers
        sections.Add(BuildProvidersSection());

        // 4. Storage Paths
        sections.Add(BuildStoragePathsSection());

        // 5. Upload
        sections.Add(BuildUploadSection());

        // 6. Downloads
        sections.Add(BuildDownloadsSection());

        // 7. Cache
        sections.Add(BuildCacheSection());

        // 7b. Redis Config (only shown when CacheProvider.Type is Redis)
        sections.Add(BuildRedisSection());

        // 8. Cron Jobs
        sections.Add(BuildCronJobsSection());

        // 9. Password Policy
        sections.Add(BuildPasswordPolicySection());

        // 10. Hub Options
        sections.Add(BuildHubOptionsSection());

        // 11. Organization Management
        sections.Add(BuildOrgManagementSection());

        // 12. Thumbnails
        sections.Add(BuildThumbnailsSection());

        // 13. Export & Zip
        sections.Add(BuildExportZipSection());

        // 14. LDAP
        sections.Add(BuildLdapSection());

        // 15. Processing Platform
        sections.Add(BuildProcessingPlatformSection());

        // 15b. Tool Gating (per-tool feature gating, part of ProcessingPlatform)
        sections.Add(BuildToolGatingSection());

        // 16. Import
        sections.Add(BuildImportSection());

        // 17. Connection Strings
        sections.Add(BuildConnectionStringsSection());

        // 18. Serilog
        sections.Add(BuildSerilogSection());

        // 19. Misc
        sections.Add(BuildMiscSection());

        return new ConfigurationDataDto { Sections = sections };
    }

    // ---------------------------------------------------------------------------
    // Helper: load defaults from embedded appsettings-default.json
    // ---------------------------------------------------------------------------

    private AppSettings LoadDefaults()
    {
        try
        {
            var efp = new EmbeddedResourceQuery();
            var executingAssembly = Assembly.GetExecutingAssembly();
            using var reader = new StreamReader(efp.Read(executingAssembly, MagicStrings.AppSettingsDefaultFileName));
            var jObject = JsonConvert.DeserializeObject<JObject>(reader.ReadToEnd());
            return jObject?["AppSettings"]?.ToObject<AppSettings>() ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    // ---------------------------------------------------------------------------
    // Helper: build a section with IsActive computed from field differences
    // ---------------------------------------------------------------------------

    private ConfigurationSectionDataDto Section(
        string name, string title, string description,
        List<ConfigurationFieldDataDto> fields)
    {
        var isActive = fields.Any(f =>
        {
            if (f.Sensitive) return f.IsSet;
            return f.CurrentValue != null && f.CurrentValue != f.DefaultValue;
        });
        return new ConfigurationSectionDataDto
        {
            Name = name,
            Title = title,
            Description = description,
            IsActive = isActive,
            Fields = fields
        };
    }

    // ---------------------------------------------------------------------------
    // Helper: build a single field DTO
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto Field(
        string key, string displayName, string fieldType, string description,
        string? currentValue = null, string? defaultValue = null,
        bool sensitive = false, bool isSet = false,
        string[]? enumOptions = null, int? minValue = null, int? maxValue = null,
        string? unit = null)
    {
        return new ConfigurationFieldDataDto
        {
            Key = key,
            DisplayName = displayName,
            FieldType = fieldType,
            Description = description,
            CurrentValue = sensitive ? null : currentValue,
            DefaultValue = sensitive ? null : defaultValue,
            Sensitive = sensitive,
            IsSet = isSet,
            EnumOptions = enumOptions,
            MinValue = minValue,
            MaxValue = maxValue,
            Unit = unit
        };
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a string property comparison
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto StringField(
        string key, string displayName, string description,
        string currentValue, string defaultValue,
        string fieldType = "text", string? unit = null)
    {
        return Field(key, displayName, fieldType, description,
            currentValue, defaultValue, unit: unit);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from an int/long property comparison
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto NumberField(
        string key, string displayName, string description,
        long currentValue, long defaultValue,
        int? minValue = null, int? maxValue = null, string? unit = null)
    {
        return Field(key, displayName, "number", description,
            currentValue.ToString(), defaultValue.ToString(),
            minValue: minValue, maxValue: maxValue, unit: unit);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a nullable int/long property comparison
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto NullableNumberField(
        string key, string displayName, string description,
        long? currentValue, long? defaultValue,
        int? minValue = null, int? maxValue = null, string? unit = null)
    {
        return Field(key, displayName, "number", description,
            currentValue?.ToString(), defaultValue?.ToString(),
            minValue: minValue, maxValue: maxValue, unit: unit);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a bool property comparison
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto BoolField(
        string key, string displayName, string description,
        bool currentValue, bool defaultValue)
    {
        return Field(key, displayName, "bool", description,
            currentValue.ToString(), defaultValue.ToString());
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a TimeSpan? property comparison
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto TimeSpanField(
        string key, string displayName, string description,
        TimeSpan? currentValue, TimeSpan? defaultValue)
    {
        return Field(key, displayName, "timespan", description,
            currentValue?.ToString(), defaultValue?.ToString());
    }

    // ---------------------------------------------------------------------------
    // Helper: build a field from a string[] property comparison
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto ArrayField(
        string key, string displayName, string description,
        string[]? currentValue, string[]? defaultValue)
    {
        var cv = currentValue != null ? string.Join(",", currentValue) : null;
        var dv = defaultValue != null ? string.Join(",", defaultValue) : null;
        return Field(key, displayName, "array", description, cv, dv);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a sensitive field (password, secret, token)
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto SensitiveField(
        string key, string displayName, string description,
        string currentValue, string defaultValue)
    {
        var isSet = !string.IsNullOrEmpty(currentValue) && currentValue != defaultValue;
        return Field(key, displayName, "password", description,
            sensitive: true, isSet: isSet);
    }

    // ---------------------------------------------------------------------------
    // Helper: build an enum field
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto EnumField(
        string key, string displayName, string description,
        string currentValue, string defaultValue, string[] enumOptions)
    {
        return Field(key, displayName, "enum", description,
            currentValue, defaultValue, enumOptions: enumOptions);
    }

    // ---------------------------------------------------------------------------
    // Helper: build a JSON field (complex object serialized as indented JSON text)
    // ---------------------------------------------------------------------------

    private ConfigurationFieldDataDto JsonField(
        string key, string displayName, string description,
        object? currentValue, object? defaultValue)
    {
        // Serialize enums by name (e.g. "Disabled") so the generated JSON is human-readable
        // and round-trips cleanly back into appsettings.json.
        var enumConverter = new Newtonsoft.Json.Converters.StringEnumConverter();
        var curr = currentValue != null
            ? JsonConvert.SerializeObject(currentValue, Formatting.Indented, enumConverter)
            : null;
        var def = defaultValue != null
            ? JsonConvert.SerializeObject(defaultValue, Formatting.Indented, enumConverter)
            : null;
        return Field(key, displayName, "json", description,
            curr, def, isSet: currentValue != null);
    }

    // ===========================================================================
    // Section builders
    // ===========================================================================

    private ConfigurationSectionDataDto BuildAuthSection()
    {
        return Section("Auth", "Auth & Security",
            "JWT authentication, token expiration, and external auth configuration.",
            [
                SensitiveField("Secret", "JWT Secret",
                    "Secret key used to sign JWT tokens. Change this to invalidate all existing tokens.",
                    _settings.Secret, _defaults.Secret),

                NumberField("TokenExpirationInDays", "Token Expiration (days)",
                    "Number of days before a JWT token expires and requires re-authentication.",
                    _settings.TokenExpirationInDays, _defaults.TokenExpirationInDays,
                    minValue: 1, unit: "days"),

                EnumField("AuthProvider", "Auth Database Provider",
                    "Database provider for the Identity (auth) database. This controls the database backend, NOT the authentication mode.",
                    _settings.AuthProvider.ToString(), _defaults.AuthProvider.ToString(),
                    ["Sqlite", "Mysql"]),

                StringField("ExternalAuthUrl", "External Auth URL",
                    "URL of an external authentication provider. When set, RemoteLoginManager is used instead of local login.",
                    _settings.ExternalAuthUrl, _defaults.ExternalAuthUrl),

                StringField("AuthCookieName", "Auth Cookie Name",
                    "Name of the HTTP cookie that stores the JWT token.",
                    _settings.AuthCookieName, _defaults.AuthCookieName),

                ArrayField("RevokedTokens", "Revoked Tokens",
                    "List of JWT token identifiers that have been explicitly revoked.",
                    _settings.RevokedTokens, _defaults.RevokedTokens)
            ]);
    }

    private ConfigurationSectionDataDto BuildDefaultAdminSection()
    {
        return Section("DefaultAdmin", "Default Admin",
            "Credentials for the default administrator account created on first run.",
            [
                StringField("Email", "Admin Email",
                    "Email address of the default admin user.",
                    _settings.DefaultAdmin.Email, _defaults.DefaultAdmin.Email),

                StringField("UserName", "Admin Username",
                    "Username of the default admin user.",
                    _settings.DefaultAdmin.UserName, _defaults.DefaultAdmin.UserName),

                SensitiveField("Password", "Admin Password",
                    "Password for the default admin account.",
                    _settings.DefaultAdmin.Password, _defaults.DefaultAdmin.Password)
            ]);
    }

    private ConfigurationSectionDataDto BuildProvidersSection()
    {
        var cacheType = _settings.CacheProvider?.Type.ToString() ?? "InMemory";
        var defaultCacheType = _defaults.CacheProvider?.Type.ToString() ?? "InMemory";

        return Section("Providers", "Database & Cache Providers",
            "Backend providers for the registry database, Hangfire jobs, and caching.",
            [
                EnumField("RegistryProvider", "Registry Database Provider",
                    "Database provider for the main Registry database (datasets, organizations, etc.).",
                    _settings.RegistryProvider.ToString(), _defaults.RegistryProvider.ToString(),
                    ["Sqlite", "Mysql"]),

                EnumField("HangfireProvider", "Hangfire Provider",
                    "Storage backend for Hangfire background jobs.",
                    _settings.HangfireProvider.ToString(), _defaults.HangfireProvider.ToString(),
                    ["InMemory", "Mysql"]),

                EnumField("CacheProvider.Type", "Cache Provider",
                    "Caching backend for thumbnails, tiles, and dataset visibility. Null means InMemory.",
                    cacheType, defaultCacheType,
                    ["InMemory", "Redis"])
            ]);
    }

    private ConfigurationSectionDataDto BuildStoragePathsSection()
    {
        return Section("StoragePaths", "Storage Paths",
            "Filesystem paths for Registry data, datasets, cache, and temporary files.",
            [
                StringField("StoragePath", "Storage Path",
                    "Root storage path for Registry data (identity DB, config, etc.).",
                    _settings.StoragePath, _defaults.StoragePath),

                StringField("DatasetsPath", "Datasets Path",
                    "Directory where dataset archives and extracted content are stored.",
                    _settings.DatasetsPath, _defaults.DatasetsPath),

                StringField("CachePath", "Cache Path",
                    "Directory for file-based cache (thumbnails, tiles, etc.).",
                    _settings.CachePath, _defaults.CachePath),

                StringField("TempPath", "Temp Path",
                    "Directory for temporary files during processing.",
                    _settings.TempPath, _defaults.TempPath)
            ]);
    }

    private ConfigurationSectionDataDto BuildUploadSection()
    {
        return Section("Upload", "Upload Settings",
            "Request body size limits, batch upload timeouts, and token configuration.",
            [
                NullableNumberField("MaxRequestBodySize", "Max Request Body Size",
                    "Maximum HTTP request body size in bytes. Null means unlimited.",
                    _settings.MaxRequestBodySize, _defaults.MaxRequestBodySize,
                    unit: "bytes"),

                TimeSpanField("UploadBatchTimeout", "Upload Batch Timeout",
                    "Maximum duration for a batch upload session before it expires.",
                    _settings.UploadBatchTimeout, _defaults.UploadBatchTimeout),

                NumberField("BatchTokenLength", "Batch Token Length",
                    "Length of randomly generated batch upload tokens. Must be at least 8.",
                    _settings.BatchTokenLength, _defaults.BatchTokenLength,
                    minValue: 8)
            ]);
    }

    private ConfigurationSectionDataDto BuildDownloadsSection()
    {
        return Section("Downloads", "Download Settings",
            "Concurrent download limits and anonymous bulk download restrictions.",
            [
                NullableNumberField("MaxConcurrentDownloadsPerUser", "Max Concurrent Downloads",
                    "Maximum simultaneous downloads per user. Null = unlimited.",
                    _settings.MaxConcurrentDownloadsPerUser, _defaults.MaxConcurrentDownloadsPerUser,
                    minValue: 1),

                BoolField("DisableAnonymousBulkDownloads", "Disable Anonymous Bulk Downloads",
                    "When true, anonymous users cannot download bulk archives (whole dataset, folders, multi-file). Single-file downloads remain allowed.",
                    _settings.DisableAnonymousBulkDownloads, _defaults.DisableAnonymousBulkDownloads)
            ]);
    }

    private ConfigurationSectionDataDto BuildCacheSection()
    {
        return Section("Cache", "Cache Expiration",
            "TTL for cached thumbnails, tiles, dataset visibility, and cache cleanup interval.",
            [
                TimeSpanField("ThumbnailsCacheExpiration", "Thumbnails Cache Expiration",
                    "How long thumbnail images are cached before regeneration.",
                    _settings.ThumbnailsCacheExpiration, _defaults.ThumbnailsCacheExpiration),

                TimeSpanField("TilesCacheExpiration", "Tiles Cache Expiration",
                    "How long map tiles are cached before regeneration.",
                    _settings.TilesCacheExpiration, _defaults.TilesCacheExpiration),

                TimeSpanField("DatasetVisibilityCacheExpiration", "Dataset Visibility Cache Expiration",
                    "How long dataset visibility settings are cached.",
                    _settings.DatasetVisibilityCacheExpiration, _defaults.DatasetVisibilityCacheExpiration),

                TimeSpanField("ClearCacheInterval", "Clear Cache Interval",
                    "How often the file cache is scanned and stale entries are removed.",
                    _settings.ClearCacheInterval, _defaults.ClearCacheInterval)
            ]);
    }

    // Only active (fields populated) when CacheProvider.Type is Redis
    private ConfigurationSectionDataDto BuildRedisSection()
    {
        var cacheType = _settings.CacheProvider?.Type.ToString() ?? "InMemory";
        if (cacheType != "Redis")
        {
            var inactive = Section("RedisConfig", "Redis Configuration",
                "Redis connection and cache settings. Only applied when Cache Provider is set to Redis. Current provider: " + cacheType + ".",
                []);
            inactive.IsActive = false;
            return inactive;
        }

        var redisSettings = _settings.CacheProvider?.Settings?.ToObject<RedisProviderSettings>();
        var redisDefaults = _defaults.CacheProvider?.Settings?.ToObject<RedisProviderSettings>();

        return Section("RedisConfig", "Redis Configuration",
            "Redis cache connection address, key prefix, and default TTL. Only used when Cache Provider is Redis.",
            [
                StringField("CacheProvider.InstanceAddress", "Redis Instance Address",
                    "Connection address for Redis (e.g., 'localhost:6379').",
                    redisSettings?.InstanceAddress, redisDefaults?.InstanceAddress),

                StringField("CacheProvider.InstanceName", "Redis Key Prefix",
                    "Instance name used as a prefix for all cache keys, enabling multi-instance isolation on a shared Redis server.",
                    redisSettings?.InstanceName, redisDefaults?.InstanceName),

                TimeSpanField("CacheProvider.Expiration", "Redis Default Expiration",
                    "Default TTL for cache entries stored in Redis, if not specified by the caller.",
                    redisSettings?.Expiration, redisDefaults?.Expiration)
            ]);
    }

    private ConfigurationSectionDataDto BuildCronJobsSection()
    {
        return Section("CronJobs", "Cron Jobs",
            "Recurring background job schedules (cron expressions) and retention periods.",
            [
                StringField("CleanupExpiredJobsCron", "Cleanup Expired Jobs Cron",
                    "Cron expression for cleaning up expired Hangfire jobs. Default: '0 * * * *' (hourly). Set to 'disabled' to remove.",
                    _settings.CleanupExpiredJobsCron, _defaults.CleanupExpiredJobsCron, "cron"),

                StringField("SyncJobIndexStatesCron", "Sync Job Index States Cron",
                    "Cron expression for reconciling JobIndex states across restarts. Default: '0 * * * *' (hourly).",
                    _settings.SyncJobIndexStatesCron, _defaults.SyncJobIndexStatesCron, "cron"),

                StringField("ProcessPendingBuildsCron", "Process Pending Builds Cron",
                    "Cron expression for retrying pending builds. Default: '0 */6 * * *' (every 6 hours).",
                    _settings.ProcessPendingBuildsCron, _defaults.ProcessPendingBuildsCron, "cron"),

                StringField("OrphanedDatasetCleanupCron", "Orphaned Dataset Cleanup Cron",
                    "Cron expression for cleaning up orphaned dataset folders. Default: '0 3 * * *' (daily at 3 AM).",
                    _settings.OrphanedDatasetCleanupCron, _defaults.OrphanedDatasetCleanupCron, "cron"),

                StringField("DatasetCleanupCron", "Dataset Cleanup Cron",
                    "Cron expression for full DDB cleanup (entries + build artifacts) on all datasets. Default: '0 0 * * *' (daily at midnight).",
                    _settings.DatasetCleanupCron, _defaults.DatasetCleanupCron, "cron"),

                StringField("ArtifactCompletenessCheckerCron", "Artifact Completeness Checker Cron",
                    "Cron expression for scanning entries and rebuilding incomplete artifacts. Default: '0 2 * * *' (daily at 2 AM).",
                    _settings.ArtifactCompletenessCheckerCron, _defaults.ArtifactCompletenessCheckerCron, "cron"),

                StringField("JobIndexCleanupCron", "Job Index Cleanup Cron",
                    "Cron expression for purging old terminal JobIndex records. Default: '0 4 * * *' (daily at 4 AM).",
                    _settings.JobIndexCleanupCron, _defaults.JobIndexCleanupCron, "cron"),

                NumberField("JobIndexRetentionDays", "Job Index Retention (days)",
                    "Number of days to keep terminal (Succeeded/Failed/Deleted) JobIndex records.",
                    _settings.JobIndexRetentionDays, _defaults.JobIndexRetentionDays,
                    minValue: 1, unit: "days"),

                NumberField("HangfireJobRetentionDays", "Hangfire Job Retention (days)",
                    "Expiration timeout for succeeded Hangfire jobs. Failed jobs are kept for diagnostics. Minimum: 1 day.",
                    _settings.HangfireJobRetentionDays, _defaults.HangfireJobRetentionDays,
                    minValue: 1, unit: "days"),

                NumberField("HangfireInvisibilityTimeoutHours", "Hangfire Invisibility Timeout (hours)",
                    "MySQL storage: hours before a running job is considered abandoned and eligible for re-dequeue. Raise for long-running builds. Minimum: 1 hour.",
                    _settings.HangfireInvisibilityTimeoutHours, _defaults.HangfireInvisibilityTimeoutHours,
                    minValue: 1, unit: "hours")
            ]);
    }

    private ConfigurationSectionDataDto BuildPasswordPolicySection()
    {
        var pp = _settings.PasswordPolicy;
        var dp = _defaults.PasswordPolicy;
        return Section("PasswordPolicy", "Password Policy",
            "Password complexity requirements. When null in config, no policy is enforced.",
            [
                NumberField("MinLength", "Minimum Length",
                    "Minimum number of characters required for passwords.",
                    pp?.MinLength ?? 0, dp?.MinLength ?? 0, minValue: 1),

                BoolField("RequireDigit", "Require Digit",
                    "Password must contain at least one numeric digit.",
                    pp?.RequireDigit ?? false, dp?.RequireDigit ?? false),

                BoolField("RequireUppercase", "Require Uppercase",
                    "Password must contain at least one uppercase letter.",
                    pp?.RequireUppercase ?? false, dp?.RequireUppercase ?? false),

                BoolField("RequireLowercase", "Require Lowercase",
                    "Password must contain at least one lowercase letter.",
                    pp?.RequireLowercase ?? false, dp?.RequireLowercase ?? false),

                BoolField("RequireNonAlphanumeric", "Require Special Character",
                    "Password must contain at least one non-alphanumeric character.",
                    pp?.RequireNonAlphanumeric ?? false, dp?.RequireNonAlphanumeric ?? false)
            ]);
    }

    private ConfigurationSectionDataDto BuildHubOptionsSection()
    {
        var ho = _settings.HubOptions;
        var do_ = _defaults.HubOptions;
        return Section("HubOptions", "Hub Options",
            "UI branding, white-label customization, and feature visibility toggles.",
            [
                StringField("AppLogo", "App Logo",
                    "URL of the navbar logo. Use /branding/... for user-supplied assets.",
                    ho?.AppLogo, do_?.AppLogo),

                StringField("AppName", "App Name",
                    "Display name used as document title. Defaults to 'DroneDB' when null.",
                    ho?.AppName, do_?.AppName),

                StringField("AppIcon", "App Icon",
                    "CSS icon class or URL shown next to app name. Defaults to 'icon-dronedb'.",
                    ho?.AppIcon, do_?.AppIcon),

                BoolField("ShowRegistrationLink", "Show Registration Link",
                    "Whether to show the registration link on the login page.",
                    ho?.ShowRegistrationLink ?? true, do_?.ShowRegistrationLink ?? true),

                BoolField("DisableDatasetCreation", "Disable Dataset Creation",
                    "Hides the 'New dataset' button and all dataset creation entry-points.",
                    ho?.DisableDatasetCreation ?? false, do_?.DisableDatasetCreation ?? false),

                BoolField("DisableStorageInfo", "Disable Storage Info",
                    "Hides the per-user storage indicator in the header.",
                    ho?.DisableStorageInfo ?? false, do_?.DisableStorageInfo ?? false),

                BoolField("DisableAccountManagement", "Disable Account Management",
                    "Hides account-management menu items. Useful with external auth.",
                    ho?.DisableAccountManagement ?? false, do_?.DisableAccountManagement ?? false),

                StringField("SingleOrganization", "Single Organization",
                    "When set, routes users directly to this organization slug (single-tenant mode).",
                    ho?.SingleOrganization, do_?.SingleOrganization),

                BoolField("ReadOnlyOrgs", "Read-Only Organizations",
                    "Hides organization create/delete/edit actions.",
                    ho?.ReadOnlyOrgs ?? false, do_?.ReadOnlyOrgs ?? false),

                JsonField("Favicon", "Favicon / Web Manifest",
                    "Favicon and web manifest configuration: FaviconIco, Favicon16, Favicon32, AppleTouchIcon, Manifest, ThemeColor. Files should be placed under {StoragePath}/branding/.",
                    ho?.Favicon, do_?.Favicon)
            ]);
    }

    private ConfigurationSectionDataDto BuildOrgManagementSection()
    {
        return Section("OrgManagement", "Organization Management",
            "Controls for organization member management and default user organizations.",
            [
                BoolField("EnableOrganizationMemberManagement", "Enable Member Management",
                    "When enabled, organization owners can manage members. When disabled, only system admins can.",
                    _settings.EnableOrganizationMemberManagement, _defaults.EnableOrganizationMemberManagement),

                BoolField("EnableDefaultUserOrganization", "Enable Default User Organization",
                    "When enabled, new users automatically get a personal default organization.",
                    _settings.EnableDefaultUserOrganization, _defaults.EnableDefaultUserOrganization)
            ]);
    }

    private ConfigurationSectionDataDto BuildThumbnailsSection()
    {
        return Section("Thumbnails", "Thumbnails",
            "Remote thumbnail generator URL, default size, and candidate file names.",
            [
                StringField("RemoteThumbnailGeneratorUrl", "Remote Thumbnail Generator URL",
                    "URL of a remote thumbnail generator service. Null uses the local generator.",
                    _settings.RemoteThumbnailGeneratorUrl, _defaults.RemoteThumbnailGeneratorUrl),

                NumberField("DefaultThumbnailSize", "Default Thumbnail Size",
                    "Default thumbnail size in pixels when no size is specified.",
                    _settings.DefaultThumbnailSize, _defaults.DefaultThumbnailSize,
                    unit: "pixels"),

                ArrayField("DatasetThumbnailCandidates", "Dataset Thumbnail Candidates",
                    "File names (in dataset root) considered as dataset thumbnail candidates, in priority order.",
                    _settings.DatasetThumbnailCandidates, _defaults.DatasetThumbnailCandidates)
            ]);
    }

    private ConfigurationSectionDataDto BuildExportZipSection()
    {
        return Section("ExportZip", "Export & Zip",
            "Memory thresholds for ZIP creation and maximum GeoTIFF export sizes.",
            [
                NumberField("MaxZipMemoryThreshold", "Max ZIP Memory Threshold",
                    "Maximum size in bytes for keeping ZIP creation in memory. Larger archives use disk.",
                    _settings.MaxZipMemoryThreshold, _defaults.MaxZipMemoryThreshold,
                    unit: "bytes"),

                NullableNumberField("MaxExportSizeBytes", "Max Export Size",
                    "Maximum estimated output size for GeoTIFF raster export. Null = unlimited.",
                    _settings.MaxExportSizeBytes, _defaults.MaxExportSizeBytes,
                    unit: "bytes")
            ]);
    }

    private ConfigurationSectionDataDto BuildLdapSection()
    {
        var ls = _settings.LdapSettings;
        var dl = _defaults.LdapSettings;
        var isActive = ls?.Enabled ?? false;

        var section = Section("LdapSettings", "LDAP / Active Directory",
            "LDAP authentication settings. Mutually exclusive with ExternalAuthUrl.",
            [
                BoolField("Enabled", "LDAP Enabled",
                    "Enables LDAP/Active Directory authentication. Mutually exclusive with ExternalAuthUrl.",
                    ls?.Enabled ?? false, dl?.Enabled ?? false),

                StringField("Server", "LDAP Server",
                    "LDAP server hostname (e.g., 'ldap.example.com' or 'dc.domain.com').",
                    ls?.Server, dl?.Server),

                NumberField("Port", "LDAP Port",
                    "LDAP port: 389 for plain LDAP, 636 for LDAPS.",
                    ls?.Port ?? 0, dl?.Port ?? 0),

                BoolField("UseSsl", "Use SSL/TLS",
                    "Use SSL/TLS (LDAPS) for the connection. Strongly recommended in production.",
                    ls?.UseSsl ?? false, dl?.UseSsl ?? false),

                BoolField("ValidateSslCertificate", "Validate SSL Certificate",
                    "Validate the server SSL certificate chain. Disable only for self-signed certs in testing.",
                    ls?.ValidateSslCertificate ?? false, dl?.ValidateSslCertificate ?? false),

                StringField("BaseDn", "Base DN",
                    "Base DN for searches (e.g., 'dc=example,dc=com').",
                    ls?.BaseDn, dl?.BaseDn),

                StringField("BindDn", "Bind DN",
                    "Service account DN for the initial search bind. Null for anonymous bind.",
                    ls?.BindDn, dl?.BindDn),

                SensitiveField("BindPassword", "Bind Password",
                    "Password for the Bind DN service account.",
                    ls?.BindPassword, dl?.BindPassword),

                StringField("UserDnFormat", "User DN Format",
                    "Optional format string for constructing user principal directly. {0} is replaced with username.",
                    ls?.UserDnFormat, dl?.UserDnFormat),

                StringField("EmailAttribute", "Email Attribute",
                    "LDAP attribute for the user email address. AD default: 'mail'.",
                    ls?.EmailAttribute, dl?.EmailAttribute),

                StringField("DisplayNameAttribute", "Display Name Attribute",
                    "LDAP attribute for the display name. AD default: 'displayName'.",
                    ls?.DisplayNameAttribute, dl?.DisplayNameAttribute),

                StringField("GroupMembershipAttribute", "Group Membership Attribute",
                    "LDAP attribute listing group memberships. Default: 'memberOf'.",
                    ls?.GroupMembershipAttribute, dl?.GroupMembershipAttribute),

                StringField("SearchFilter", "Search Filter",
                    "LDAP search filter to locate user entry. {0} is replaced with the username.",
                    ls?.SearchFilter, dl?.SearchFilter, "text"),

                ArrayField("AdminGroupDns", "Admin Group DN's",
                    "Distinguished names of LDAP groups whose members receive the Registry admin role.",
                    ls?.AdminGroupDns, dl?.AdminGroupDns),

                NumberField("Timeout", "LDAP Timeout",
                    "Timeout in seconds for LDAP operations.",
                    ls?.Timeout ?? 0, dl?.Timeout ?? 0, unit: "seconds")
            ]);

        // Override IsActive: LDAP section is active when Enabled == true
        section.IsActive = isActive;
        return section;
    }

    private ConfigurationSectionDataDto BuildProcessingPlatformSection()
    {
        var pp = _settings.ProcessingPlatform;
        var dp = _defaults.ProcessingPlatform;
        var isActive = pp != null;

        var section = Section("ProcessingPlatform", "Processing Platform",
            "Task substrate settings: concurrency limits, dedup, NodeODX nodes, and bulk download thresholds.",
            [
                NumberField("ArtifactTtlHours", "Artifact TTL (hours)",
                    "Hours before a produced artifact's WorkDir is swept.",
                    pp?.ArtifactTtlHours ?? dp?.ArtifactTtlHours ?? 0,
                    dp?.ArtifactTtlHours ?? 0, unit: "hours"),

                NumberField("MaxConcurrentTasksPerUser", "Max Concurrent Tasks / User",
                    "Maximum concurrent heavy tasks per user.",
                    pp?.MaxConcurrentTasksPerUser ?? dp?.MaxConcurrentTasksPerUser ?? 0,
                    dp?.MaxConcurrentTasksPerUser ?? 0),

                NumberField("MaxQueuedTasksPerUser", "Max Queued Tasks / User",
                    "Maximum queued (pending) heavy tasks per user.",
                    pp?.MaxQueuedTasksPerUser ?? dp?.MaxQueuedTasksPerUser ?? 0,
                    dp?.MaxQueuedTasksPerUser ?? 0),

                NumberField("MaxConcurrentTasksPerOrg", "Max Concurrent Tasks / Org",
                    "Maximum concurrent heavy tasks per organization.",
                    pp?.MaxConcurrentTasksPerOrg ?? dp?.MaxConcurrentTasksPerOrg ?? 0,
                    dp?.MaxConcurrentTasksPerOrg ?? 0),

                NumberField("MaxConcurrentTasksGlobal", "Max Concurrent Tasks (global)",
                    "Hard cap on concurrent heavy tasks across all users.",
                    pp?.MaxConcurrentTasksGlobal ?? dp?.MaxConcurrentTasksGlobal ?? 0,
                    dp?.MaxConcurrentTasksGlobal ?? 0),

                NumberField("MaxEstimatedOutputBytesPerSubmit", "Max Estimated Output / Submit",
                    "Hard cap on estimated output size per task submission.",
                    pp?.MaxEstimatedOutputBytesPerSubmit ?? dp?.MaxEstimatedOutputBytesPerSubmit ?? 0,
                    dp?.MaxEstimatedOutputBytesPerSubmit ?? 0, unit: "bytes"),

                NumberField("MaxArchiveExtractSizeBytes", "Max Archive Extract Size",
                    "Maximum compressed archive size accepted by archive-extract tool.",
                    pp?.MaxArchiveExtractSizeBytes ?? dp?.MaxArchiveExtractSizeBytes ?? 0,
                    dp?.MaxArchiveExtractSizeBytes ?? 0, unit: "bytes"),

                NumberField("DiskSafetyMarginBytes", "Disk Safety Margin",
                    "Disk head-room kept free during archive extraction. 0 to disable.",
                    pp?.DiskSafetyMarginBytes ?? dp?.DiskSafetyMarginBytes ?? 0,
                    dp?.DiskSafetyMarginBytes ?? 0, unit: "bytes"),

                NumberField("DefaultRasterTileSize", "Default Raster Tile Size",
                    "Default tile size in pixels for windowed raster export.",
                    pp?.DefaultRasterTileSize ?? dp?.DefaultRasterTileSize ?? 0,
                    dp?.DefaultRasterTileSize ?? 0, unit: "pixels"),

                NumberField("BulkDownloadAsyncThresholdBytes", "Bulk Download Async Threshold",
                    "Size threshold above which bulk downloads are offloaded to async task.",
                    pp?.BulkDownloadAsyncThresholdBytes ?? dp?.BulkDownloadAsyncThresholdBytes ?? 0,
                    dp?.BulkDownloadAsyncThresholdBytes ?? 0, unit: "bytes"),

                NumberField("MaxConcurrentBulkDownloadsPerUser", "Max Concurrent Bulk Downloads / User",
                    "Maximum active bulk-download tasks per user.",
                    pp?.MaxConcurrentBulkDownloadsPerUser ?? dp?.MaxConcurrentBulkDownloadsPerUser ?? 0,
                    dp?.MaxConcurrentBulkDownloadsPerUser ?? 0),

                BoolField("DedupEnabled", "Dedup Enabled",
                    "Enable task deduplication (prevent duplicate submissions).",
                    pp?.DedupEnabled ?? dp?.DedupEnabled ?? false,
                    dp?.DedupEnabled ?? false),

                NumberField("DedupLookbackHours", "Dedup Lookback (hours)",
                    "Time window for deduplication checks.",
                    pp?.DedupLookbackHours ?? dp?.DedupLookbackHours ?? 0,
                    dp?.DedupLookbackHours ?? 0, unit: "hours"),

                NumberField("LogTailMaxLines", "Log Tail Max Lines",
                    "Maximum lines returned by log tail endpoint.",
                    pp?.LogTailMaxLines ?? dp?.LogTailMaxLines ?? 0,
                    dp?.LogTailMaxLines ?? 0),

                NumberField("LogTailMaxBytes", "Log Tail Max Bytes",
                    "Maximum bytes returned by log tail endpoint.",
                    pp?.LogTailMaxBytes ?? dp?.LogTailMaxBytes ?? 0,
                    dp?.LogTailMaxBytes ?? 0, unit: "bytes"),

                NumberField("ProgressUpdateThrottleSeconds", "Progress Update Throttle",
                    "Minimum seconds between progress updates.",
                    pp?.ProgressUpdateThrottleSeconds ?? dp?.ProgressUpdateThrottleSeconds ?? 0,
                    dp?.ProgressUpdateThrottleSeconds ?? 0, unit: "seconds"),

                NumberField("RemoteNodePollIntervalSeconds", "Remote Node Poll Interval",
                    "Poll interval for remote NodeODX nodes.",
                    pp?.RemoteNodePollIntervalSeconds ?? dp?.RemoteNodePollIntervalSeconds ?? 0,
                    dp?.RemoteNodePollIntervalSeconds ?? 0, unit: "seconds"),

                NumberField("RemoteNodePollMaxBackoffSeconds", "Remote Node Max Backoff",
                    "Maximum backoff for remote node polling.",
                    pp?.RemoteNodePollMaxBackoffSeconds ?? dp?.RemoteNodePollMaxBackoffSeconds ?? 0,
                    dp?.RemoteNodePollMaxBackoffSeconds ?? 0, unit: "seconds"),

                NumberField("RemoteNodeRequestTimeoutSeconds", "Remote Node Request Timeout",
                    "HTTP request timeout for remote NodeODX nodes.",
                    pp?.RemoteNodeRequestTimeoutSeconds ?? dp?.RemoteNodeRequestTimeoutSeconds ?? 0,
                    dp?.RemoteNodeRequestTimeoutSeconds ?? 0, unit: "seconds"),

                NumberField("MaxConcurrentUrlImportsPerUser", "Max Concurrent URL Imports / User",
                    "Maximum active import-file (single-file URL import) tasks per user.",
                    pp?.MaxConcurrentUrlImportsPerUser ?? dp?.MaxConcurrentUrlImportsPerUser ?? 0,
                    dp?.MaxConcurrentUrlImportsPerUser ?? 0),

                JsonField("OrgDailyOutputBytes", "Per-Org Daily Output Budget (bytes)",
                    "Per-organization daily output size budget in bytes, keyed by org slug ('default' is the fallback).",
                    pp?.OrgDailyOutputBytes, dp?.OrgDailyOutputBytes),

                JsonField("NodeOdx", "NodeODX Processing Nodes",
                    "Array of NodeODX processing node configurations. Each entry: { Id, Url, Token, Title }.",
                    pp?.NodeOdx, dp?.NodeOdx)
            ]);

        section.IsActive = isActive;
        return section;
    }

    private ConfigurationSectionDataDto BuildToolGatingSection()
    {
        var pp = _settings.ProcessingPlatform;
        var isActive = pp?.Tools is { Count: > 0 };

        // Section name is "ProcessingPlatform" so the client merges the generated JSON
        // under the same AppSettings:ProcessingPlatform node as the main section.
        var section = Section("ProcessingPlatform", "Tool Gating",
            "Per-tool feature gating: hide or disable specific heavy tools, and restrict them by role or organization.",
            [
                JsonField("Tools", "Tool Gating Config",
                    "JSON object keyed by tool id (e.g. \"photogrammetry\"). Each entry accepts: " +
                    "availability (Enabled|Disabled|Hidden), disabledMessage, allowedRoles (array), " +
                    "allowedOrgs (array), hideWhenNotAllowed (bool), maxConcurrentPerUser, maxQueuedPerUser. " +
                    "Omit a tool to use the default (Enabled, no restrictions).",
                    isActive ? pp!.Tools : null,
                    null)
            ]);

        section.IsActive = isActive;
        return section;
    }

    private ConfigurationSectionDataDto BuildImportSection()
    {
        var imp = _settings.Import;
        var dImp = _defaults.Import;
        var isActive = imp != null;

        var section = Section("Import", "Import Dataset",
            "Import settings: size limits, allowed source types, SSRF guard, timeouts, and concurrency.",
            [
                NumberField("MaxImportSizeBytes", "Max Import Size",
                    "Maximum total bytes that can be imported in a single task. 0 = unlimited.",
                    imp?.MaxImportSizeBytes ?? dImp?.MaxImportSizeBytes ?? 0,
                    dImp?.MaxImportSizeBytes ?? 0, unit: "bytes"),

                ArrayField("AllowedSourceTypes", "Allowed Source Types",
                    "Allowed import source types. Null/empty = all sources allowed. Values: 'registry', 'archive-url'.",
                    imp?.AllowedSourceTypes, dImp?.AllowedSourceTypes),

                BoolField("SsrfAllowPrivateNetworks", "SSRF: Allow Private Networks",
                    "Allow outbound connections to private/loopback/link-local addresses.",
                    imp?.SsrfAllowPrivateNetworks ?? dImp?.SsrfAllowPrivateNetworks ?? false,
                    dImp?.SsrfAllowPrivateNetworks ?? false),

                ArrayField("SsrfAllowedHosts", "SSRF: Allowed Hosts",
                    "Hostnames explicitly exempt from SSRF blocking.",
                    imp?.SsrfAllowedHosts, dImp?.SsrfAllowedHosts),

                NumberField("MaxRedirects", "Max Redirects",
                    "Maximum HTTP redirects to follow during import.",
                    imp?.MaxRedirects ?? dImp?.MaxRedirects ?? 0,
                    dImp?.MaxRedirects ?? 0),

                NumberField("ConnectTimeoutSeconds", "Connect Timeout",
                    "Connection/authentication timeout per source.",
                    imp?.ConnectTimeoutSeconds ?? dImp?.ConnectTimeoutSeconds ?? 0,
                    dImp?.ConnectTimeoutSeconds ?? 0, unit: "seconds"),

                NumberField("TransferTimeoutSeconds", "Transfer Timeout",
                    "Total transfer timeout per import task.",
                    imp?.TransferTimeoutSeconds ?? dImp?.TransferTimeoutSeconds ?? 0,
                    dImp?.TransferTimeoutSeconds ?? 0, unit: "seconds"),

                NumberField("RegistryDownloadConcurrency", "Registry Download Concurrency",
                    "Number of files downloaded in parallel from a remote registry.",
                    imp?.RegistryDownloadConcurrency ?? dImp?.RegistryDownloadConcurrency ?? 0,
                    dImp?.RegistryDownloadConcurrency ?? 0),

                NumberField("RegistryDownloadMaxRetries", "Registry Download Max Retries",
                    "Maximum retry attempts per file download against a remote registry.",
                    imp?.RegistryDownloadMaxRetries ?? dImp?.RegistryDownloadMaxRetries ?? 0,
                    dImp?.RegistryDownloadMaxRetries ?? 0),

                NumberField("MaxFileImportSizeBytes", "Max Single-File Import Size",
                    "Maximum size of a single file imported from URL (0 = fall back to MaxImportSizeBytes).",
                    imp?.MaxFileImportSizeBytes ?? dImp?.MaxFileImportSizeBytes ?? 0,
                    dImp?.MaxFileImportSizeBytes ?? 0, unit: "bytes"),

                NumberField("MinDownloadSpeedBytesPerSec", "Min Download Speed",
                    "Minimum sustained download speed before import is aborted (0 = disable).",
                    imp?.MinDownloadSpeedBytesPerSec ?? dImp?.MinDownloadSpeedBytesPerSec ?? 0,
                    dImp?.MinDownloadSpeedBytesPerSec ?? 0, unit: "bytes/s"),

                NumberField("LowSpeedGraceSeconds", "Low-Speed Grace Period",
                    "Time window for low-speed detection and hard stall timeout.",
                    imp?.LowSpeedGraceSeconds ?? dImp?.LowSpeedGraceSeconds ?? 0,
                    dImp?.LowSpeedGraceSeconds ?? 0, unit: "seconds"),

                ArrayField("BlockedFileExtensions", "Blocked File Extensions",
                    "File extensions rejected by all ingestion paths (block-list). Ignored when AllowedFileExtensions is non-empty.",
                    imp?.BlockedFileExtensions, dImp?.BlockedFileExtensions),

                ArrayField("AllowedFileExtensions", "Allowed File Extensions",
                    "File extensions accepted by all ingestion paths (allow-list). When non-empty, switches to allow-list mode and ignores BlockedFileExtensions.",
                    imp?.AllowedFileExtensions, dImp?.AllowedFileExtensions)
            ]);

        section.IsActive = isActive;
        return section;
    }

    private ConfigurationSectionDataDto BuildConnectionStringsSection()
    {
        // Connection strings are at the top level of appsettings.json, not under AppSettings.
        // Read current values from IConfiguration and defaults from embedded resource.
        var currentIdentity = _configuration.GetConnectionString("IdentityConnection")
            ?? "Data Source=./identity.db;Mode=ReadWriteCreate";
        var currentRegistry = _configuration.GetConnectionString("RegistryConnection")
            ?? "Data Source=./registry.db;Mode=ReadWriteCreate";
        var defaultIdentity = "Data Source=./identity.db;Mode=ReadWriteCreate";
        var defaultRegistry = "Data Source=./registry.db;Mode=ReadWriteCreate";

        return Section("ConnectionStrings", "Connection Strings",
            "Database connection strings for Identity and Registry databases.",
            [
                SensitiveField("IdentityConnection", "Identity Connection String",
                    "Connection string for the Identity (auth) database.",
                    currentIdentity, defaultIdentity),

                SensitiveField("RegistryConnection", "Registry Connection String",
                    "Connection string for the main Registry database.",
                    currentRegistry, defaultRegistry)
            ]);
    }

    private ConfigurationSectionDataDto BuildSerilogSection()
    {
        // Serilog config is at the top level of appsettings.json, not under AppSettings.
        // We provide the default values from appsettings-default.json.
        var defaultLogPath = "./logs/registry.txt";
        var defaultMinLevel = "Warning";

        return Section("Serilog", "Serilog Logging",
            "Logging configuration: minimum level and file output path.",
            [
                StringField("MinimumLevel", "Minimum Log Level",
                    "Minimum severity level for log output. Values: Verbose, Debug, Information, Warning, Error, Fatal.",
                    defaultMinLevel, defaultMinLevel),

                StringField("LogPath", "Log File Path",
                    "Path to the log file output.",
                    defaultLogPath, defaultLogPath)
            ]);
    }

    private ConfigurationSectionDataDto BuildMiscSection()
    {
        return Section("Misc", "Miscellaneous",
            "Worker threads, CORS origins, external URL override, random name length, and data protection keys.",
            [
                NumberField("WorkerThreads", "Worker Threads",
                    "Number of worker threads. 0 or -1 uses ASP.NET default.",
                    _settings.WorkerThreads, _defaults.WorkerThreads),

                ArrayField("AllowedOrigins", "Allowed CORS Origins",
                    "Allowed CORS origins. Null or empty = all origins allowed.",
                    _settings.AllowedOrigins, _defaults.AllowedOrigins),

                StringField("ExternalUrlOverride", "External URL Override",
                    "Overrides the external URL used in generated links and redirects.",
                    _settings.ExternalUrlOverride, _defaults.ExternalUrlOverride),

                NumberField("RandomDatasetNameLength", "Random Dataset Name Length",
                    "Length of randomly generated dataset names. Must be at least 8.",
                    _settings.RandomDatasetNameLength, _defaults.RandomDatasetNameLength,
                    minValue: 8),

                BoolField("EnableStorageLimiter", "Enable Storage Limiter",
                    "Enables per-user storage quota enforcement.",
                    _settings.EnableStorageLimiter, _defaults.EnableStorageLimiter),

                StringField("DataProtectionKeysPath", "Data Protection Keys Path",
                    "Path to shared directory for ASP.NET Core Data Protection keys. Required when processing node runs separately without Redis.",
                    _settings.DataProtectionKeysPath, _defaults.DataProtectionKeysPath),

                SensitiveField("MonitorToken", "Monitor Token",
                    "Token for health check endpoints (/health, /quickhealth).",
                    _settings.MonitorToken, _defaults.MonitorToken)
            ]);
    }
}
