#nullable enable
using System.Collections.Frozen;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// IDs of the recurring Hangfire jobs that this application registers itself
/// (<see cref="HangfireJobsInitializer"/>). Housekeeping must never remove them:
/// they are re-created only at the next start.
/// </summary>
public static class RecurringJobIds
{
    public const string CleanupExpiredJobs = "cleanup-expired-jobs";
    public const string SyncJobIndexStates = "sync-jobindex-states";
    public const string RecoverIndexedJobPublications = "recover-indexed-job-publications";
    public const string ProcessPendingBuilds = "process-pending-builds";
    public const string CleanupOrphanedDatasets = "cleanup-orphaned-datasets";
    public const string CleanupOldJobIndices = "cleanup-old-jobindices";
    public const string RecurringDatasetCleanup = "recurring-dataset-cleanup";
    public const string CheckArtifactCompleteness = "check-artifact-completeness";
    public const string IndexReconciliation = "index-reconciliation";
    public const string CheckUpdates = "check-updates";

    /// <summary>Every recurring job ID owned by the application (ordinal comparison, as Hangfire stores them).</summary>
    public static readonly FrozenSet<string> Owned = new[]
    {
        CleanupExpiredJobs,
        SyncJobIndexStates,
        RecoverIndexedJobPublications,
        ProcessPendingBuilds,
        CleanupOrphanedDatasets,
        CleanupOldJobIndices,
        RecurringDatasetCleanup,
        CheckArtifactCompleteness,
        IndexReconciliation,
        CheckUpdates
    }.ToFrozenSet();
}
