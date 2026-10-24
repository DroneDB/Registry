#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Services.Ports;

using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Service for cleaning up deleted datasets.
/// Handles cancellation of active build jobs, removal of JobIndex entries,
/// and filesystem cleanup in a background job.
/// </summary>
public class DatasetCleanupService
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly IJobIndexQuery _jobIndexQuery;
    private readonly ILogger<DatasetCleanupService> _logger;
    private readonly IDatasetIndexQueue? _indexQueue;

    // Active Hangfire job states that should be cancelled. These string values map directly to
    // Hangfire's built-in job state names (e.g., Created, Enqueued, Processing, Scheduled, Awaiting).
    // Only "active"/non-terminal states are included here; terminal states such as Succeeded/Failed/Deleted
    // are intentionally excluded because they should not be cancelled. If Hangfire adds or changes
    // job states, update this list accordingly based on Hangfire's job state documentation.
    // See: https://docs.hangfire.io/en/latest/background-methods/using-job-states.html
    private static readonly string[] ActiveJobStates = ["Created", "Enqueued", "Processing", "Scheduled", "Awaiting"];

    public DatasetCleanupService(
        RegistryContext context,
        IDdbManager ddbManager,
        IBackgroundJobsProcessor backgroundJob,
        IJobIndexQuery jobIndexQuery,
        ILogger<DatasetCleanupService> logger,
        IDatasetIndexQueue? indexQueue = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _backgroundJob = backgroundJob ?? throw new ArgumentNullException(nameof(backgroundJob));
        _jobIndexQuery = jobIndexQuery ?? throw new ArgumentNullException(nameof(jobIndexQuery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _indexQueue = indexQueue;
    }

    /// <summary>
    /// Cleans up a deleted dataset by cancelling its active jobs, removing its JobIndex entries and deleting the
    /// filesystem folder. The dataset's job rows were moved to its tombstone slug
    /// (<see cref="SlugKeyedRows.TombstoneSlug"/>) when it was deleted, so this acts on the rows and jobs of the dataset
    /// that was deleted, never on those of another dataset that has taken its slug since.
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="internalRef">Dataset internal reference (folder GUID)</param>
    /// <param name="context">Hangfire context for logging</param>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 300])]
    public Task CleanupDeletedAsync(string orgSlug, Guid internalRef, PerformContext? context = null) =>
        CleanupAsync(orgSlug, SlugKeyedRows.TombstoneSlug(internalRef), internalRef, context);

    /// <summary>
    /// The job as it was queued before its rows moved to a tombstone: it names the dataset's slug, so it still runs
    /// with the behaviour it was queued with (jobs already in Hangfire storage; remove after one release, like the
    /// other stored shapes of <c>HangfireUtils</c>). It cannot tell the deleted dataset's rows from those of a dataset
    /// that has taken the slug since.
    /// </summary>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 300])]
    public Task CleanupDeletedDatasetAsync(string orgSlug, string dsSlug, Guid internalRef,
        PerformContext? context = null) =>
        CleanupAsync(orgSlug, dsSlug, internalRef, context);

    // The cleanup of the rows of (orgSlug, dsSlug): dsSlug is the dataset's slug for a job queued before the tombstone, its tombstone since
    private async Task CleanupAsync(string orgSlug, string dsSlug, Guid internalRef, PerformContext? context)
    {
        var console = JobConsole.For(_logger, context);
        console.Info("Starting cleanup for deleted dataset {OrgSlug}/{DsSlug} (InternalRef: {InternalRef})",
            orgSlug, dsSlug, internalRef);

        // 1. Cancel all active jobs for this dataset
        var cancelledCount = await CancelActiveJobsAsync(orgSlug, dsSlug, console);
        console.Info("Cancelled {Count} active jobs", cancelledCount);

        // 2. Remove all JobIndex entries for this dataset
        var removedCount = await RemoveJobIndexEntriesAsync(orgSlug, dsSlug);
        console.Info("Removed {Count} JobIndex entries", removedCount);

        // 2.5. Release the dataset's per-dataset index lane so it is not retained after the
        //      dataset ceases to exist (new enqueues transparently recreate a fresh lane for
        //      the remainder of the shutdown).
        if (_indexQueue != null)
        {
            _indexQueue.Release(new DatasetKey(orgSlug, internalRef));
            console.Info("Released index lane for the removed dataset");
        }

        // 3. Delete filesystem folder
        try
        {
            _ddbManager.Delete(orgSlug, internalRef);
            console.Info("Deleted filesystem folder for {OrgSlug}/{InternalRef}", orgSlug, internalRef);
        }
        catch (Exception ex)
        {
            // Don't rethrow - the orphaned folder cleanup job will handle it
            console.Warning(ex, "Failed to delete filesystem folder for {OrgSlug}/{InternalRef}: {Error}. " +
                                "Will be cleaned up by orphaned folder cleanup job", orgSlug, internalRef, ex.Message);
        }

        console.Info("Cleanup completed for {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
    }

    /// <summary>
    /// Cancels all active jobs for the specified dataset.
    /// </summary>
    private async Task<int> CancelActiveJobsAsync(string orgSlug, string dsSlug, JobConsole console)
    {
        var jobs = await _jobIndexQuery.GetByOrgDsAsync(orgSlug, dsSlug, take: int.MaxValue);
        var activeJobs = jobs.Where(j => ActiveJobStates.Contains(j.CurrentState)).ToList();

        var cancelledCount = 0;
        foreach (var job in activeJobs)
        {
            try
            {
                if (_backgroundJob.Delete(job.JobId))
                {
                    cancelledCount++;
                    _logger.LogDebug("Cancelled job {JobId} (state: {State}) for deleted dataset {OrgSlug}/{DsSlug}",
                        job.JobId, job.CurrentState, orgSlug, dsSlug);
                }
            }
            catch (Exception ex)
            {
                console.Warning(ex, "Failed to cancel job {JobId}: {Error}", job.JobId, ex.Message);
            }
        }

        return cancelledCount;
    }

    /// <summary>
    /// Removes all JobIndex entries for the specified dataset.
    /// </summary>
    private async Task<int> RemoveJobIndexEntriesAsync(string orgSlug, string dsSlug)
    {
        var jobIndicesToRemove = await _context.JobIndices
            .Where(j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug)
            .ToListAsync();

        if (jobIndicesToRemove.Count > 0)
        {
            _context.JobIndices.RemoveRange(jobIndicesToRemove);
            await _context.SaveChangesAsync();
        }

        return jobIndicesToRemove.Count;
    }
}
