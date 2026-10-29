using System;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Recurring service that runs DDB cleanup (entries + build artifacts) on every
/// dataset of every organization, and deletes what interrupted uploads left behind
/// (abandoned push folders and upload temp files). Scheduled via Hangfire (cron from
/// <see cref="Registry.Web.Models.Configuration.JobScheduleOptions.DatasetCleanupCron"/>).
/// </summary>
/// <remarks>
/// One run at a time across the hosts: a second one would clean the same datasets in parallel
/// (libddb serializes them as busy). Timeout 0: the run in progress does the work.
/// </remarks>
[AutomaticRetry(Attempts = 1)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class RecurringDatasetCleanupService
{
    private readonly DatasetSweep _sweep;
    private readonly IDdbManager _ddbManager;
    private readonly IFileSystem _fileSystem;
    private readonly TimeProvider _time;
    private readonly ILogger<RecurringDatasetCleanupService> _logger;

    public RecurringDatasetCleanupService(
        DatasetSweep sweep,
        IDdbManager ddbManager,
        IFileSystem fileSystem,
        ILogger<RecurringDatasetCleanupService> logger,
        TimeProvider? time = null)
    {
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The job as stored by earlier releases: runs with the context's shutdown token.</summary>
    public Task CleanupAllDatasetsAsync(PerformContext? context) =>
        CleanupAllDatasetsAsync(context, JobCancellation.Of(context));

    public async Task CleanupAllDatasetsAsync(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);
        console.Info("Starting recurring dataset cleanup across all organizations");

        var totalEntries = 0;
        var totalBuilds = 0;
        var totalUploads = 0;

        var result = await _sweep.ForEachAsync(console, "Cleanup", (ds, _) =>
        {
            var ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
            var cleanup = ddb.Cleanup();

            var entries = cleanup.Entries?.Length ?? 0;
            var builds = cleanup.Builds?.Length ?? 0;
            var uploads = DatasetUploads.RemoveAbandoned(_fileSystem, ddb.DatasetFolderPath,
                _time.GetUtcNow().UtcDateTime, DatasetUploads.AbandonedAfter);
            totalEntries += entries;
            totalBuilds += builds;
            totalUploads += uploads;

            if (entries > 0 || builds > 0 || uploads > 0)
                console.Info("Cleaned {Dataset}: removed {Entries} entries, {Builds} build artifacts, {Uploads} abandoned uploads",
                    ds.Key, entries, builds, uploads);
            return Task.CompletedTask;
        }, cancellationToken);

        console.Info("Recurring dataset cleanup completed: {Succeeded}/{Datasets} datasets processed, {Failed} failed, " +
                     "{Entries} entries removed, {Builds} build artifacts removed, {Uploads} abandoned uploads removed",
            result.Succeeded, result.Datasets, result.Failed, totalEntries, totalBuilds, totalUploads);
    }
}
