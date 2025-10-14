using System;

namespace Registry.Web.Models.DTO;

public class BuildPendingStatusDto
{
    /// <summary>
    /// Last time the job was executed
    /// </summary>
    public DateTime? LastRun { get; set; }

    /// <summary>
    /// Total number of datasets processed in last run
    /// </summary>
    public int TotalDatasets { get; set; }

    /// <summary>
    /// Number of datasets checked in last run (not skipped by cache)
    /// </summary>
    public int DatasetsChecked { get; set; }

    /// <summary>
    /// Number of datasets skipped in last run (cache optimization)
    /// </summary>
    public int DatasetsSkipped { get; set; }

    /// <summary>
    /// Number of datasets with pending builds found in last run
    /// </summary>
    public int PendingBuildsFound { get; set; }

    /// <summary>
    /// Number of build jobs enqueued in last run
    /// </summary>
    public int JobsEnqueued { get; set; }

    /// <summary>
    /// Number of errors encountered in last run
    /// </summary>
    public int Errors { get; set; }

    /// <summary>
    /// Duration of last run in milliseconds
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Cache hit rate percentage (0-100)
    /// </summary>
    public double CacheHitRate => TotalDatasets > 0
        ? (double)DatasetsSkipped / TotalDatasets * 100
        : 0;

    /// <summary>
    /// Whether the job is currently enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Next scheduled run time (estimated based on 1-minute interval)
    /// </summary>
    public DateTime? NextScheduledRun => LastRun?.AddMinutes(1);
}
