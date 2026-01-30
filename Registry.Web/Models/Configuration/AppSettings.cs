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
    /// Cache provider settings
    /// </summary>
    public CacheProvider CacheProvider { get; set; }

    /// <summary>
    /// Enables the user storage limiter
    /// </summary>
    public bool EnableStorageLimiter { get; set; }

    /// <summary>
    /// Number of worker threads (0 to use ASP.NET default)
    /// </summary>
    public int WorkerThreads { get; set; }

    /// <summary>
    /// File cache path
    /// </summary>
    public string CachePath { get; set; }

    /// <summary>
    /// Remote thumbnail generator url (if null the local one will be used)
    /// </summary>
    public string RemoteThumbnailGeneratorUrl { get; set; }

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
    /// Clear cache interval
    /// </summary>
    public TimeSpan? ClearCacheInterval { get; set; }

    /// <summary>
    /// Monitor token to call health checks
    /// </summary>
    public string MonitorToken { get; set; }

    /// <summary>
    /// Maximum size in bytes for keeping ZIP creation in memory.
    /// Files larger than this will use temporary files on disk.
    /// Default: 1GB (1073741824 bytes)
    /// </summary>
    public long MaxZipMemoryThreshold { get; set; } = 1073741824; // 1GB

    /// <summary>
    /// Cron expression for cleanup expired jobs task
    /// Default: Daily (Cron.Daily)
    /// </summary>
    public string CleanupExpiredJobsCron { get; set; }

    /// <summary>
    /// Cron expression for sync job index states task
    /// Default: "*/5 * * * *" (every 5 minutes)
    /// </summary>
    public string SyncJobIndexStatesCron { get; set; }

    /// <summary>
    /// Cron expression for process pending builds task
    /// Default: "* * * * *" (every minute)
    /// </summary>
    public string ProcessPendingBuildsCron { get; set; }

    /// <summary>
    /// Cron expression for orphaned dataset folder cleanup task
    /// Default: "0 3 * * *" (daily at 3:00 AM)
    /// </summary>
    public string OrphanedDatasetCleanupCron { get; set; }

}