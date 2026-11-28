namespace Registry.Web.Models.Configuration;

/// <summary>
/// The schedules of the recurring jobs and the retention of job records (keys of the <c>AppSettings</c>
/// section). A cron left empty takes the job's default; "disabled", "off" or "none" removes the job
/// (<c>HangfireJobsInitializer</c> owns both).
/// </summary>
public sealed class JobScheduleOptions
{
    public const int DefaultJobIndexRetentionDays = 60;
    public const int DefaultHangfireJobRetentionDays = 2;
    public const int DefaultHangfireInvisibilityTimeoutHours = 4;

    // The ages of the cache cleanup: libddb's own defaults, so the job removes what a tile request's occasional
    // cleanup would, only every night instead of one request in a thousand
    public const int DefaultCacheCleanupTileDays = 5;
    public const int DefaultCacheCleanupDownloadDays = 7;
    public const int DefaultCacheCleanupDsmDays = 30;
    public const int DefaultCacheCleanupTempDays = 2;

    /// <summary>
    /// The schedule of each recurring job whose cron is left empty, one constant per cron setting with the
    /// setting's name: the shipped settings files leave them unset, so these are the schedules a deployment runs.
    /// </summary>
    public static class DefaultCrons
    {
        public const string CleanupExpiredJobsCron = "0 0 * * *";
        public const string SyncJobIndexStatesCron = "0 * * * *";
        public const string RecoverIndexedJobPublicationsCron = "* * * * *";
        public const string ProcessPendingBuildsCron = "0 */6 * * *";
        public const string OrphanedDatasetCleanupCron = "0 3 * * *";
        public const string DatasetCleanupCron = "0 0 * * *";
        public const string ArtifactCompletenessCheckerCron = "0 2 * * *";
        public const string JobIndexCleanupCron = "0 4 * * *";
        public const string IndexReconciliationCron = "0 * * * *";

        // Half past four: the last nightly sweep that walks the disk starts at 3:00 (orphaned folders) and the one at
        // 4:00 only touches the database, and minute 30 misses the hourly jobs
        public const string CacheCleanupCron = "30 4 * * *";
    }

    /// <summary>Cleanup of expired Hangfire jobs. Default: "0 0 * * *" (daily at midnight).</summary>
    public string? CleanupExpiredJobsCron { get; set; }

    /// <summary>
    /// JobIndex reconciliation safety net. JobIndex rows are updated in real time by
    /// <c>JobIndexStateFilter</c>; this sweep only reconciles entries that missed a transition (e.g.
    /// across a restart). Default: "0 * * * *" (hourly).
    /// </summary>
    public string? SyncJobIndexStatesCron { get; set; }

    /// <summary>
    /// Recovery of indexed jobs whose publication was interrupted (left in the non-runnable
    /// <c>Indexing</c> state). Default: "* * * * *" (every minute).
    /// </summary>
    public string? RecoverIndexedJobPublicationsCron { get; set; }

    /// <summary>
    /// Pending-builds safety net: pending builds are retried event-driven, this is a backstop for
    /// retries lost to a node restart. Default: "0 */6 * * *" (every 6 hours).
    /// </summary>
    public string? ProcessPendingBuildsCron { get; set; }

    /// <summary>Orphaned dataset folder cleanup. Default: "0 3 * * *" (daily at 3:00).</summary>
    public string? OrphanedDatasetCleanupCron { get; set; }

    /// <summary>DDB <c>cleanup</c> (entries + build artifacts) of every dataset. Default: "0 0 * * *" (daily at midnight).</summary>
    public string? DatasetCleanupCron { get; set; }

    /// <summary>
    /// Artifact completeness checker: enqueues a rebuild for any buildable entry whose output is
    /// incomplete (e.g. after a build-output format migration). Default: "0 2 * * *" (daily at 2:00).
    /// </summary>
    public string? ArtifactCompletenessCheckerCron { get; set; }

    /// <summary>JobIndex cleanup (removes old terminal records). Default: "0 4 * * *" (daily at 4:00).</summary>
    public string? JobIndexCleanupCron { get; set; }

    /// <summary>
    /// Index reconciliation sweep: re-enqueues unindexed on-disk files, reports (never deletes) indexed
    /// entries missing on disk, ages out quarantined files. Default: "0 * * * *" (hourly).
    /// </summary>
    public string? IndexReconciliationCron { get; set; }

    /// <summary>
    /// Cleanup of libddb's cache and of its folders in the temporary folder: rendered tiles, downloaded copies of
    /// remote rasters, DSM tiles and what interrupted requests left, each past its age below. Default: "30 4 * * *"
    /// (daily at 4:30).
    /// </summary>
    public string? CacheCleanupCron { get; set; }

    /// <summary>Days after their last change the folders of rendered tiles are removed (0 to 36500). Default: 5.</summary>
    public int CacheCleanupTileDays { get; set; } = DefaultCacheCleanupTileDays;

    /// <summary>
    /// Days after their last change the copies of remote rasters that tile requests downloaded are removed (0 to
    /// 36500). Default: 7.
    /// </summary>
    public int CacheCleanupDownloadDays { get; set; } = DefaultCacheCleanupDownloadDays;

    /// <summary>Days after their last change the elevation tiles of the DSM service are removed (0 to 36500). Default: 30.</summary>
    public int CacheCleanupDsmDays { get; set; } = DefaultCacheCleanupDsmDays;

    /// <summary>
    /// Days after their last change the folders a crashed or interrupted libddb request left (in the cache and in the
    /// temporary folder) are removed (0 to 36500). Default: 2.
    /// </summary>
    public int CacheCleanupTempDays { get; set; } = DefaultCacheCleanupTempDays;

    /// <summary>Days terminal (Succeeded/Failed/Deleted) JobIndex records are kept. Default: 60.</summary>
    public int JobIndexRetentionDays { get; set; } = DefaultJobIndexRetentionDays;

    /// <summary>
    /// Expiration of succeeded and other non-failed terminal Hangfire job records (at least 1 day);
    /// low values reduce the row churn that inflates the InnoDB shared tablespace. Default: 2.
    /// </summary>
    public int HangfireJobRetentionDays { get; set; } = DefaultHangfireJobRetentionDays;

    /// <summary>
    /// MySQL storage invisibility timeout in hours: a job running longer may be dequeued again by
    /// another worker. Raise it when builds can take longer. Default: 4.
    /// </summary>
    public int HangfireInvisibilityTimeoutHours { get; set; } = DefaultHangfireInvisibilityTimeoutHours;
}
