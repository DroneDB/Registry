#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Recurring safety-net sweep. Per dataset:
/// re-enqueues on-disk files that are not indexed, reports (never deletes) indexed entries whose
/// file is missing on disk, and ages out old quarantined files
/// (see <see cref="Managers.ObjectsManager"/>'s <c>.uploads/quarantine</c> compensation).
/// Scheduled via Hangfire (cron from <see cref="JobScheduleOptions.IndexReconciliationCron"/>).
/// </summary>
/// <remarks>
/// One run at a time across the hosts: the hourly sweep walks every dataset folder and can outlast
/// the hour on the HDD RAID5; a second run would queue the same unindexed files from another host.
/// Timeout 0: the run in progress does the work, the next tick picks up the rest. Not retried as a
/// whole: the next scheduled run is the retry for a failure in a single dataset.
/// </remarks>
[AutomaticRetry(Attempts = 0)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class IndexReconciliationService
{
    private readonly record struct ReconcileSummary(
        int Unindexed, int Reindexed, int Missing, int QuarantineRemoved, long DurationMs);

    private readonly DatasetSweep _sweep;
    private readonly IDdbManager _ddbManager;
    private readonly IDatasetIndexQueue _indexQueue;
    private readonly ReconciliationSettings _settings;
    private readonly IFileSystem _fs;
    private readonly DatasetPathPolicy _paths;
    private readonly ILogger<IndexReconciliationService> _logger;

    public IndexReconciliationService(
        DatasetSweep sweep,
        IDdbManager ddbManager,
        IDatasetIndexQueue indexQueue,
        IOptions<ReconciliationSettings> settings,
        IFileSystem fs,
        DatasetPathPolicy paths,
        ILogger<IndexReconciliationService> logger)
    {
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _indexQueue = indexQueue ?? throw new ArgumentNullException(nameof(indexQueue));
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Value;
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The job as stored by earlier releases: runs with the context's shutdown token.</summary>
    public Task ReconcileAllDatasetsAsync(PerformContext? context) =>
        ReconcileAllDatasetsAsync(context, JobCancellation.Of(context));

    /// <summary>Runs the reconciliation sweep across every dataset of every organization.</summary>
    public async Task ReconcileAllDatasetsAsync(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);
        console.Info("Starting index reconciliation sweep across all organizations");

        await _sweep.ForEachAsync(console, "Reconciliation", async (ds, ct) =>
        {
            // Narrowed to the index role interface - reconciliation only reads/writes the
            // index, never build/meta/raster/analytics.
            IDdbIndex ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
            var summary = await ReconcileDatasetAsync(ds.OrgSlug, ds.InternalRef, ddb, console, ct);

            console.Info("[{Dataset}] unindexed={Unindexed} reindexed={Reindexed} missing={Missing} " +
                         "quarantineRemoved={QuarantineRemoved} durationMs={DurationMs}",
                ds.Key, summary.Unindexed, summary.Reindexed, summary.Missing, summary.QuarantineRemoved,
                summary.DurationMs);
        }, cancellationToken);

        console.Info("Index reconciliation sweep completed");
    }

    private async Task<ReconcileSummary> ReconcileDatasetAsync(string orgSlug, Guid internalRef, IDdbIndex ddb,
        JobConsole console, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = ddb.DatasetFolderPath;

        // 1. Enumerate files on disk with the one dataset walker (DatasetPathPolicy.EnumerateFiles):
        //    the reserved folders are pruned during the walk, so the live database
        //    (.ddb at any depth) and the staged/quarantined uploads (the root .uploads) are never
        //    scanned and never count as "unindexed"; a nested ".uploads" is an ordinary user folder.
        var onDisk = new HashSet<string>(_paths.EnumerateFiles(_fs, root), StringComparer.Ordinal);

        // 2. Compare against the index (files only - directories have no on-disk counterpart to diff).
        var indexed = new HashSet<string>(
            ddb.FindSummaries(EntryQuery.All)
                .Where(e => e.Type != EntryType.Directory)
                .Select(e => e.Path),
            StringComparer.Ordinal);

        var unindexedPaths = onDisk.Except(indexed).ToArray();
        var missingPaths = indexed.Except(onDisk).ToArray();

        // 3. Unindexed on disk -> re-enqueue through the same coalescing lane uploads use, capped per run.
        var toReindex = unindexedPaths.Take(_settings.MaxItemsPerRun).ToArray();
        var reindexed = 0;
        if (toReindex.Length > 0)
        {
            try
            {
                await _indexQueue.EnqueueAsync(new DatasetKey(orgSlug, internalRef), toReindex, ct);
                reindexed = toReindex.Length;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                console.Info("Failed to re-index {Count} unindexed file(s): {Error}", toReindex.Length, ex.Message);
            }
        }

        // 4. Indexed but missing on disk -> report only. Never destroy data automatically.
        if (missingPaths.Length > 0)
        {
            var sample = string.Join(", ", missingPaths.Take(10)) + (missingPaths.Length > 10 ? ", …" : "");
            console.Info("{Count} indexed entries missing on disk (report only): {Sample}", missingPaths.Length, sample);
        }

        // 5. Age out old quarantined files.
        var quarantineRemoved = AgeOutQuarantine(root, console);

        sw.Stop();
        return new ReconcileSummary(unindexedPaths.Length, reindexed, missingPaths.Length, quarantineRemoved,
            sw.ElapsedMilliseconds);
    }

    private int AgeOutQuarantine(string datasetRoot, JobConsole console)
    {
        var quarantineDir = Path.Combine(datasetRoot, IDDB.UploadsFolderName, IDDB.QuarantineFolderName);
        if (!_fs.FolderExists(quarantineDir))
            return 0;

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, _settings.QuarantineRetentionDays));
        var removed = 0;

        foreach (var file in _fs.EnumerateFiles(quarantineDir))
        {
            try
            {
                if (_fs.GetLastWriteTime(file).ToUniversalTime() < cutoff)
                {
                    _fs.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                console.Info("Failed to remove aged quarantine file '{File}': {Error}", file, ex.Message);
            }
        }

        return removed;
    }
}
