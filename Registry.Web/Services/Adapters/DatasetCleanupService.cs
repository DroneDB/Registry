using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// The cleanup of a deleted dataset, in a background job: it stops the dataset's queued and running jobs
/// (<see cref="TaskCanceller"/>), removes their rows and deletes the dataset's folder.
/// <para>
/// The folder is deleted only when no worker of the dataset is still running: a task stopped while it runs (a native
/// rescan cannot be interrupted, an import keeps copying until it sees the cancel) may still write there. A row that names
/// a live job server (<see cref="TaskWorkerMarker"/>, <see cref="ILiveJobServers"/>) is kept with the folder, and that
/// worker queues this cleanup again when it stops. A mark left by a server that crashed does not count: the job store drops
/// the server from the live list after its heartbeat timeout.
/// </para>
/// </summary>
public class DatasetCleanupService
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly TaskCanceller _canceller;
    private readonly ILiveJobServers _servers;
    private readonly ILogger<DatasetCleanupService> _logger;
    private readonly IDatasetIndexQueue? _indexQueue;

    public DatasetCleanupService(
        RegistryContext context,
        IDdbManager ddbManager,
        TaskCanceller canceller,
        ILiveJobServers servers,
        ILogger<DatasetCleanupService> logger,
        IDatasetIndexQueue? indexQueue = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _canceller = canceller ?? throw new ArgumentNullException(nameof(canceller));
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _indexQueue = indexQueue;
    }

    /// <summary>Queues the cleanup of the dataset <paramref name="internalRef"/> of <paramref name="orgSlug"/>, deleted (<see cref="CleanupDeletedAsync"/>); the job id.</summary>
    public static string Queue(IBackgroundJobsProcessor jobs, string orgSlug, Guid internalRef) =>
        jobs.Enqueue<DatasetCleanupService>(service => service.CleanupDeletedAsync(orgSlug, internalRef, null));

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

        var rows = await _context.JobIndices.AsNoTracking()
            .Where(j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug)
            .Select(j => new { j.JobId, j.CurrentState, j.WorkerServerId })
            .ToArrayAsync();

        // 1. Stop the jobs that are queued or running
        var cancelledCount = 0;
        foreach (var job in rows.Where(j => SlugKeyedRows.BlockingJobStates.Contains(j.CurrentState)))
            await BestEffort.RunAsync(async () =>
            {
                if (await _canceller.CancelAsync(job.JobId, orgSlug, dsSlug))
                    cancelledCount++;
            }, ex => console.Warning(ex, "Failed to cancel job {JobId}: {Error}", job.JobId, ex.Message));
        console.Info("Cancelled {Count} active jobs", cancelledCount);

        // 2. The rows whose worker is still running stay (the worker queues this cleanup again when it stops); the
        //    others are removed
        var live = rows.Any(j => j.WorkerServerId != null) ? _servers.Ids() : null;
        var running = rows.Where(j => j.WorkerServerId != null && live!.Contains(j.WorkerServerId))
            .Select(j => j.JobId).ToArray();
        var removedCount = await _context.JobIndices
            .Where(j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug && !running.Contains(j.JobId))
            .ExecuteDeleteAsync();
        console.Info("Removed {Count} JobIndex entries", removedCount);

        // 3. Release the dataset's per-dataset index lane so it is not retained after the
        //    dataset ceases to exist (new enqueues transparently recreate a fresh lane for
        //    the remainder of the shutdown).
        if (_indexQueue != null)
        {
            _indexQueue.Release(new DatasetKey(orgSlug, internalRef));
            console.Info("Released index lane for the removed dataset");
        }

        // 4. Delete the filesystem folder, once no worker can write there
        if (running.Length > 0)
        {
            console.Info("Kept the folder of {OrgSlug}/{InternalRef}: {Count} task(s) still running on a worker " +
                         "({Tasks}); the last one to stop queues this cleanup again", orgSlug, internalRef,
                running.Length, string.Join(", ", running));
            return;
        }

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
}
