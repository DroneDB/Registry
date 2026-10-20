using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Registry.Common;

namespace Registry.Web.Models.Configuration;

public class AppSettings
{
    /// <summary>
    /// Secret to generate JWT tokens
    /// </summary>
    public string Secret { get; set; }

    /// <summary>
    /// JWT token expiration in days
    /// </summary>
    public int TokenExpirationInDays { get; set; }

    /// <summary>
    /// List of JWT revoked tokens
    /// </summary>
    public string[] RevokedTokens { get; set; }

    /// <summary>
    /// Provider for authentication database
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public DbProvider AuthProvider { get; set; }

    /// <summary>
    /// Provider for registry database
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public DbProvider RegistryProvider { get; set; }

    /// <summary>
    /// MySQL server version used by the MySql providers, e.g. "8.0.36-mysql" or "10.11.6-mariadb".
    /// When set, the server is never probed for its version; when empty it is auto-detected once
    /// per connection string on first use (see <c>MySqlServerVersionCache</c>).
    /// </summary>
    public string MySqlServerVersion { get; set; }

    /// <summary>
    /// Provider for hangfire
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public HangfireProvider HangfireProvider { get; set; }

    /// <summary>
    /// Default admin details
    /// </summary>
    public AdminInfo DefaultAdmin { get; set; }

    /// <summary>
    /// Main storage path
    /// </summary>
    public string StoragePath { get; set; }

    /// <summary>
    /// Storage path for datasets
    /// </summary>
    public string DatasetsPath { get; set; }

    /// <summary>
    /// Storage path for temp files
    /// </summary>
    public string TempPath { get; set; }

    /// <summary>
    /// Max request body size
    /// </summary>
    public long? MaxRequestBodySize { get; set; }

    /// <summary>
    /// Lenght of batch tokens
    /// </summary>
    public int BatchTokenLength { get; set; }

    /// <summary>
    /// Timeout of batch uploads
    /// </summary>
    public TimeSpan UploadBatchTimeout { get; set; }

    /// <summary>
    /// Length of the random generated dataset name
    /// </summary>
    public int RandomDatasetNameLength { get; set; }

    /// <summary>
    /// Name of the auth cookie that contains the jwt token
    /// </summary>
    public string AuthCookieName { get; set; }

    /// <summary>
    /// Overrides the external url
    /// </summary>
    public string ExternalUrlOverride { get; set; }

    /// <summary>
    /// External authentication provider url
    /// </summary>
    public string ExternalAuthUrl { get; set; }

    /// <summary>
    /// LDAP/Active Directory authentication settings.
    /// Mutually exclusive with <see cref="ExternalAuthUrl"/>.
    /// </summary>
    public LdapSettings LdapSettings { get; set; }

    /// <summary>
    /// Cache provider settings
    /// </summary>
    public CacheProvider CacheProvider { get; set; }

    /// <summary>
    /// Enables the user storage limiter
    /// </summary>
    public bool EnableStorageLimiter { get; set; }

    /// <summary>
    /// Maximum number of concurrent downloads per user (or per IP for anonymous users).
    /// Null = unlimited (feature disabled). Minimum value: 1.
    /// </summary>
    public int? MaxConcurrentDownloadsPerUser { get; set; }

    /// <summary>
    /// When true, anonymous (not logged-in) users are not allowed to download bulk archives
    /// (whole dataset, folders, or multi-file selections). Single-file downloads remain allowed.
    /// Default: false (anonymous bulk downloads are permitted on public/unlisted datasets).
    /// </summary>
    public bool DisableAnonymousBulkDownloads { get; set; } = false;

    /// <summary>
    /// Enables organization-level member management.
    /// When disabled, only system admins can manage organization members.
    /// </summary>
    public bool EnableOrganizationMemberManagement { get; set; } = false;

    /// <summary>
    /// Enables the automatic creation of a default personal organization when a new user is created.
    /// When false, new users will not get a default organization; organizations must be assigned manually.
    /// </summary>
    public bool EnableDefaultUserOrganization { get; set; } = true;

    /// <summary>
    /// Number of worker threads (0 to use ASP.NET default). When set, it is also the default
    /// number of light background workers (<see cref="ProcessingBudgetSettings.LightWorkers"/>).
    /// </summary>
    public int WorkerThreads { get; set; }

    /// <summary>
    /// Concurrency and native resource budget of background jobs (REG-09).
    /// </summary>
    public ProcessingBudgetSettings ProcessingBudget { get; set; } = new();

    /// <summary>
    /// File cache path
    /// </summary>
    public string CachePath { get; set; }

    /// <summary>
    /// Remote thumbnail generator url (if null the local one will be used)
    /// </summary>
    public string RemoteThumbnailGeneratorUrl { get; set; }

    /// <summary>
    /// Shared secret between the web server and the ThumbnailGenerator host (header
    /// <c>X-Thumbnail-Token</c>). Generated on first start when empty and stored in appsettings.json,
    /// like <see cref="Secret"/>; the ThumbnailGenerator host refuses to start without it.
    /// </summary>
    public string ThumbnailGeneratorToken { get; set; }

    /// <summary>
    /// Default thumbnail size in pixels when no size is specified
    /// </summary>
    public int DefaultThumbnailSize { get; set; } = 512;

    /// <summary>
    /// File names (in dataset root) considered as dataset thumbnail candidates.
    /// The first matching file found is used as the dataset thumbnail.
    /// </summary>
    public string[] DatasetThumbnailCandidates { get; set; } =
    [
        "thumbnail.webp", "thumbnail.jpg", "thumbnail.png",
        "cover.webp", "cover.jpg", "cover.png"
    ];

    /// <summary>
    /// Thumbnails cache expiration
    /// </summary>
    public TimeSpan? ThumbnailsCacheExpiration { get; set; }

    /// <summary>
    /// Tiles cache expiration
    /// </summary>
    public TimeSpan? TilesCacheExpiration { get; set; }

    /// <summary>
    /// Dataset visibility cache expiration
    /// </summary>
    public TimeSpan? DatasetVisibilityCacheExpiration { get; set; }

    /// <summary>
    /// How long a user's authorization facts (exists, admin, deactivated) are reused across
    /// requests. Role changes and deletions made by this instance take effect at once; the
    /// expiration bounds changes made elsewhere (other instances, direct database edits).
    /// Zero disables the cache. Default: 30 seconds.
    /// </summary>
    public TimeSpan? AuthorizationCacheExpiration { get; set; }

    /// <summary>
    /// How long a dataset lookup by slug (dataset + organization row) is reused across requests.
    /// Changes saved by this instance take effect at once; the expiration bounds changes made
    /// elsewhere (processing nodes, direct database edits). Zero disables it. Default: 30 seconds.
    /// </summary>
    public TimeSpan? DatasetLookupCacheExpiration { get; set; }

    /// <summary>
    /// Clear cache interval
    /// </summary>
    public TimeSpan? ClearCacheInterval { get; set; }

    /// <summary>
    /// Monitor token to call health checks
    /// </summary>
    public string MonitorToken { get; set; }

    /// <summary>
    /// Maximum size in bytes for GeoTIFF raster export operations (multispectral and thermal).
    /// Estimated as width × height × bytesPerPixel × bandCount of the source raster.
    /// Null means unlimited (no enforcement, backwards compatible).
    /// Default: 1GB (1073741824 bytes).
    /// </summary>
    public long? MaxExportSizeBytes { get; set; } = 1073741824; // 1GB

    /// <summary>
    /// Cron expression for cleanup expired jobs task.
    /// Default (when null, empty, or omitted): Hangfire's daily cron.
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string CleanupExpiredJobsCron { get; set; }

    /// <summary>
    /// Cron expression for the JobIndex reconciliation safety-net task. JobIndex
    /// rows are updated in real time by <c>JobIndexStateFilter</c>; this sweep only
    /// reconciles entries that missed a transition (e.g. across a restart), so it
    /// runs infrequently to avoid background-job churn.
    /// Default (when null, empty, or omitted): "0 * * * *" (hourly).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string SyncJobIndexStatesCron { get; set; }

    /// <summary>
    /// Cron expression for the recovery of indexed jobs whose publication was interrupted
    /// (left in the non-runnable <c>Indexing</c> state, e.g. by a crash right after creation).
    /// Each run completes the publication of due jobs on their original job id.
    /// Default (when null, empty, or omitted): "* * * * *" (every minute).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string RecoverIndexedJobPublicationsCron { get; set; }

    /// <summary>
    /// Cron expression for the pending-builds safety-net sweep. Pending builds are
    /// now retried event-driven (a delayed retry is self-scheduled whenever a build
    /// leaves items pending), so this recurring job is only a low-frequency backstop
    /// for retries lost to a node restart or leftover from before the upgrade.
    /// Default (when null, empty, or omitted): "0 */6 * * *" (every 6 hours).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string ProcessPendingBuildsCron { get; set; }

    /// <summary>
    /// Cron expression for orphaned dataset folder cleanup task.
    /// Default (when null, empty, or omitted): "0 3 * * *" (daily at 3:00 AM).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string OrphanedDatasetCleanupCron { get; set; }

    /// <summary>
    /// Cron expression for the recurring full-cleanup task that runs DDB
    /// <c>cleanup</c> (entries + build artifacts) on every dataset in every organization.
    /// Default (when null, empty, or omitted): "0 0 * * *" (daily at midnight).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string DatasetCleanupCron { get; set; }

    /// <summary>
    /// Cron expression for the recurring artifact completeness checker that
    /// scans every entry in every dataset and enqueues a rebuild for any
    /// buildable entry whose output is incomplete (missing or zero-byte
    /// artifacts). Useful after a build-output format migration (e.g. FGB→MVT,
    /// EPT→COPC) to bring the corpus to the new layout without a manual sweep.
    /// Default (when null, empty, or omitted): "0 2 * * *" (daily at 2:00 AM).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string ArtifactCompletenessCheckerCron { get; set; }

    /// <summary>
    /// Cron expression for job index cleanup task (removes old terminal records).
    /// Default (when null, empty, or omitted): "0 4 * * *" (daily at 4:00 AM).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string JobIndexCleanupCron { get; set; }

    /// <summary>
    /// Number of days to retain terminal (Succeeded/Failed/Deleted) JobIndex records.
    /// Records older than this are purged by the cleanup job.
    /// Default: 60
    /// </summary>
    public int JobIndexRetentionDays { get; set; } = 60;

    /// <summary>
    /// Global Hangfire job retention (the expiration timeout applied to succeeded
    /// and other non-failed terminal job records). Keeping this low sharply reduces
    /// the row churn that inflates the InnoDB shared tablespace under frequent
    /// background jobs. Failed jobs are exempt and kept for diagnostics.
    /// Minimum enforced value is 1 day. Default: 2.
    /// </summary>
    public int HangfireJobRetentionDays { get; set; } = 2;

    /// <summary>
    /// MySQL storage invisibility timeout in hours. When using the MySQL Hangfire
    /// provider, a job that runs longer than this timeout may be re-dequeued by
    /// another worker because the storage backend treats it as abandoned. Raise
    /// this value when builds can take longer than the default (e.g. large point
    /// clouds or meshes). Default: 4 (hours).
    /// </summary>
    public int HangfireInvisibilityTimeoutHours { get; set; } = 4;

    /// <summary>
    /// Password complexity policy. When null, no password requirements are enforced.
    /// </summary>
#nullable enable
    public PasswordPolicy? PasswordPolicy { get; set; }
#nullable restore

    /// <summary>
    /// Hub UI branding and customization options exposed to the frontend at runtime.
    /// </summary>
#nullable enable
    public HubOptions? HubOptions { get; set; }
#nullable restore

    /// <summary>
    /// Allowed CORS origins. When null or empty, all origins are allowed.
    /// </summary>
#nullable enable
    public string[]? AllowedOrigins { get; set; }
#nullable restore

    /// <summary>
    /// IP addresses of the reverse proxies whose <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>
    /// headers are trusted (the client address and scheme they forward replace the connection's).
    /// When null or empty, <see cref="DefaultKnownProxies"/> (loopback, a proxy on the same host)
    /// applies. When set, it replaces the default: list loopback too if the proxy also connects
    /// over it.
    /// </summary>
#nullable enable
    public string[]? KnownProxies { get; set; }
#nullable restore

    /// <summary>The reverse proxies trusted when <see cref="KnownProxies"/> is not set.</summary>
    public static readonly string[] DefaultKnownProxies = ["127.0.0.1", "::1"];

    /// <summary>
    /// Networks (CIDR, e.g. <c>172.16.0.0/12</c>) whose addresses are trusted as reverse proxies, in
    /// addition to <see cref="KnownProxies"/>. Needed when the proxy's address is not fixed: behind a
    /// port published by Docker the app sees the bridge gateway (<c>172.x.0.1</c>) as the peer, not
    /// loopback. Null or empty: no network is trusted.
    /// </summary>
#nullable enable
    public string[]? KnownNetworks { get; set; }
#nullable restore

    /// <summary>
    /// Processing Platform (Layer 1 task substrate) settings. When null, defaults apply.
    /// </summary>
#nullable enable
    public ProcessingPlatformSettings? ProcessingPlatform { get; set; }
#nullable restore

    /// <summary>
    /// Import Dataset feature settings. When null, defaults apply.
    /// </summary>
#nullable enable
    public ImportSettings? Import { get; set; }
#nullable restore

    /// <summary>
    /// Per-dataset index write queue settings (coalesces concurrent add requests into
    /// short-lived native batches). When null, defaults apply.
    /// </summary>
#nullable enable
    public IndexQueueSettings? IndexQueue { get; set; }

    /// <summary>Concurrency of native renders (tiles, thumbnails, coverages) in this process.</summary>
    public NativeConcurrencySettings? NativeConcurrency { get; set; }

    /// <summary>Tuning of the upload pipeline.</summary>
    public UploadSettings? Uploads { get; set; }
#nullable restore

    /// <summary>
    /// Recurring index reconciliation sweep settings (unindexed re-index, missing report,
    /// quarantine ageing). When null, defaults apply.
    /// </summary>
#nullable enable
    public ReconciliationSettings? Reconciliation { get; set; }
#nullable restore

    /// <summary>
    /// Cron expression for the recurring index reconciliation sweep. Re-enqueues unindexed
    /// on-disk files, reports (never deletes) indexed entries missing on disk, and ages out
    /// quarantined files. Default (when null, empty, or omitted): "0 * * * *" (hourly).
    /// Set to "disabled", "off", or "none" to remove the job.
    /// </summary>
    public string IndexReconciliationCron { get; set; }

    /// <summary>
    /// Path to a shared directory for ASP.NET Core Data Protection key storage. Required when the
    /// processing node runs in a separate process and Redis caching is not used, so the worker can
    /// decrypt credentials the web host encrypted. When Redis is the cache provider the key ring is
    /// shared via Redis instead and this can be left null.
    /// </summary>
#nullable enable
    public string? DataProtectionKeysPath { get; set; }
#nullable restore
}