#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Services.Ports;

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

    // Active job states that should be cancelled
    private static readonly string[] ActiveJobStates = ["Created", "Enqueued", "Processing", "Scheduled", "Awaiting"];

    public DatasetCleanupService(
        RegistryContext context,
        IDdbManager ddbManager,
        IBackgroundJobsProcessor backgroundJob,
        IJobIndexQuery jobIndexQuery,
        ILogger<DatasetCleanupService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _backgroundJob = backgroundJob ?? throw new ArgumentNullException(nameof(backgroundJob));
        _jobIndexQuery = jobIndexQuery ?? throw new ArgumentNullException(nameof(jobIndexQuery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Cleans up a deleted dataset by cancelling active jobs, removing JobIndex entries,
    /// and deleting the filesystem folder.
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="dsSlug">Dataset slug</param>
    /// <param name="internalRef">Dataset internal reference (folder GUID)</param>
    /// <param name="context">Hangfire context for logging</param>
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 300])]
    public async Task CleanupDeletedDatasetAsync(
        string orgSlug,
        string dsSlug,
        Guid internalRef,
        PerformContext? context = null)
    {
        void WriteLine(string message)
        {
            _logger.LogInformation(message);
            context?.WriteLine(message);
        }

        WriteLine($"Starting cleanup for deleted dataset {orgSlug}/{dsSlug} (InternalRef: {internalRef})");

        // 1. Cancel all active jobs for this dataset
        var cancelledCount = await CancelActiveJobsAsync(orgSlug, dsSlug, context);
        WriteLine($"Cancelled {cancelledCount} active jobs");

        // 2. Remove all JobIndex entries for this dataset
        var removedCount = await RemoveJobIndexEntriesAsync(orgSlug, dsSlug);
        WriteLine($"Removed {removedCount} JobIndex entries");

        // 3. Delete filesystem folder
        try
        {
            _ddbManager.Delete(orgSlug, internalRef);
            WriteLine($"Deleted filesystem folder for {orgSlug}/{internalRef}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete filesystem folder for {OrgSlug}/{InternalRef}. Will be cleaned up by orphaned folder cleanup job",
                orgSlug, internalRef);
            context?.WriteLine($"WARNING: Failed to delete filesystem folder: {ex.Message}");
            // Don't rethrow - the orphaned folder cleanup job will handle it
        }

        WriteLine($"Cleanup completed for {orgSlug}/{dsSlug}");
    }

    /// <summary>
    /// Cancels all active jobs for the specified dataset.
    /// </summary>
    private async Task<int> CancelActiveJobsAsync(string orgSlug, string dsSlug, PerformContext? context)
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
                _logger.LogWarning(ex, "Failed to cancel job {JobId}", job.JobId);
                context?.WriteLine($"WARNING: Failed to cancel job {job.JobId}: {ex.Message}");
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
