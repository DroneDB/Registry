using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Datasets;

/// <summary>
/// The removal of a dataset once its deletion is allowed: the row leaves the database at once (its job rows move to a
/// slug nobody can have), the listings forget it, and a background job cleans up its jobs and its folder. The user
/// delete (<see cref="DatasetCommands.Delete"/>, after the access check) and the system delete of a dataset that a
/// rolled-back task created (<see cref="RemoveAsSystemAsync"/>, FR-XC-27) are this one path. Registered on every job
/// host.
/// <para>
/// A task that imports the dataset or rescans its index (<see cref="IDatasetActivity"/>) is stopped at once
/// (<see cref="TaskCanceller"/>): an import can then no longer commit. The cleanup stops the other jobs, and deletes the
/// folder only once no worker of the dataset is still running.
/// </para>
/// </summary>
public sealed class DatasetRemoval
{
    private readonly RegistryContext _context;
    private readonly IDatasetListingInvalidator _listings;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly IDatasetActivity _activity;
    private readonly TaskCanceller _canceller;
    private readonly ILogger<DatasetRemoval> _logger;

    public DatasetRemoval(RegistryContext context, IDatasetListingInvalidator listings,
        IBackgroundJobsProcessor backgroundJob, IDatasetActivity activity, TaskCanceller canceller,
        ILogger<DatasetRemoval> logger)
    {
        _context = context;
        _listings = listings;
        _backgroundJob = backgroundJob;
        _activity = activity;
        _canceller = canceller;
        _logger = logger;
    }

    /// <summary>
    /// Removes the dataset whose folder id is <paramref name="internalRef"/> without the access check of a user: the
    /// system deletes it on behalf of <paramref name="reason"/> (logged). False when no dataset has that folder id.
    /// </summary>
    public async Task<bool> RemoveAsSystemAsync(Guid internalRef, string reason, CancellationToken ct = default)
    {
        var ds = await _context.Datasets.AsTracking().Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.InternalRef == internalRef, ct);
        if (ds is null)
            return false;

        _logger.LogWarning("System delete of dataset {OrgSlug}/{DsSlug} ({InternalRef}): {Reason}",
            ds.Organization.Slug, ds.Slug, internalRef, reason);
        await RemoveAsync(ds.Organization.Slug, ds);
        return true;
    }

    /// <summary>Removes <paramref name="ds"/> of <paramref name="orgSlug"/> (tracked by this context).</summary>
    public async Task RemoveAsync(string orgSlug, Dataset ds)
    {
        var dsSlug = ds.Slug;
        // Save references before removing from DB
        var internalRef = ds.InternalRef;

        // Remove from database immediately (fast operation). Its job rows leave with it, to a slug nobody can have: the
        // cleanup below runs later, and by then another dataset may hold this slug (a rename or a move into it) with
        // its own history and jobs, which a cleanup by slug would delete and cancel
        _context.Datasets.Remove(ds);
        await _context.FollowDatasetAsync(orgSlug, dsSlug, orgSlug, SlugKeyedRows.TombstoneSlug(internalRef),
            CancellationToken.None);
        await _context.SaveChangesAsync(CancellationToken.None);

        // It leaves every listing
        await _listings.ListingChangedAsync(orgSlug, internalRef, CancellationToken.None);

        // The import or the rescan that holds it stops now, not when the cleanup runs: from here on an import cannot
        // commit. After the rows moved: a worker that stops from now on finds the tombstone (TaskWorkerMarker)
        await StopLockingTasksAsync(orgSlug, SlugKeyedRows.TombstoneSlug(internalRef));

        // Schedule background cleanup job for: cancelling active jobs, removing JobIndex entries, deleting filesystem
        var jobId = BestEffort.Get<string?>(() => DatasetCleanupService.Queue(_backgroundJob, orgSlug, internalRef), ex =>
        {
            _logger.LogError(ex,
                "Failed to enqueue cleanup job for dataset {OrgSlug}/{DsSlug} with internalRef {InternalRef}",
                orgSlug, dsSlug, internalRef);
            return null;
        });

        if (string.IsNullOrEmpty(jobId))
        {
            _logger.LogError(
                "Cleanup job could not be scheduled for dataset {OrgSlug}/{DsSlug} with internalRef {InternalRef}. " +
                "Orphaned folder cleanup will handle this later.",
                orgSlug, dsSlug, internalRef);
        }
        else
        {
            _logger.LogInformation(
                "Dataset {OrgSlug}/{DsSlug} removed from DB, cleanup job scheduled with JobId {JobId}",
                orgSlug, dsSlug, jobId);
        }
    }

    // The locking tasks of the deleted dataset (its rows are under its tombstone slug): a failure to stop one is logged,
    // the cleanup tries again
    private async Task StopLockingTasksAsync(string orgSlug, string tombstone)
    {
        foreach (var taskId in await _activity.ActiveTaskIdsAsync(orgSlug, tombstone, CancellationToken.None))
            await BestEffort.RunAsync(async () =>
            {
                if (await _canceller.CancelAsync(taskId, orgSlug, tombstone, CancellationToken.None))
                    _logger.LogInformation("Stopped task {TaskId} of the deleted dataset {OrgSlug}/{Tombstone}",
                        taskId, orgSlug, tombstone);
            }, ex => _logger.LogWarning(ex, "Cannot stop task {TaskId} of the deleted dataset {OrgSlug}/{Tombstone}: " +
                                           "its cleanup tries again", taskId, orgSlug, tombstone));
    }
}
