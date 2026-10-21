#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
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
public class IndexReconciliationService
{
    private readonly record struct ReconcileSummary(
        int Unindexed, int Reindexed, int Missing, int QuarantineRemoved, long DurationMs);

    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IDatasetIndexQueue _indexQueue;
    private readonly ReconciliationSettings _settings;
    private readonly IFileSystem _fs;
    private readonly DatasetPathPolicy _paths;
    private readonly ILogger<IndexReconciliationService> _logger;

    public IndexReconciliationService(
        RegistryContext context,
        IDdbManager ddbManager,
        IDatasetIndexQueue indexQueue,
        IOptions<ReconciliationSettings> settings,
        IFileSystem fs,
        DatasetPathPolicy paths,
        ILogger<IndexReconciliationService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _indexQueue = indexQueue ?? throw new ArgumentNullException(nameof(indexQueue));
        _settings = (settings ?? throw new ArgumentNullException(nameof(settings))).Value;
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs the reconciliation sweep across every dataset of every organization. The next
    /// scheduled run is the retry for a failure in a single dataset, so this is not automatically
    /// retried as a whole (mirrors <see cref="BuildPendingService"/>/<see cref="RecurringDatasetCleanupService"/>).
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task ReconcileAllDatasetsAsync(PerformContext? context = null)
    {
        void WriteLine(string message)
        {
            _logger.LogInformation(message);
            context?.WriteLine(message);
        }

        WriteLine("Starting index reconciliation sweep across all organizations");

        var datasets = await _context.Datasets
            .AsNoTracking()
            .Include(d => d.Organization)
            .Select(d => new { d.Slug, OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToArrayAsync();

        foreach (var ds in datasets)
        {
            var key = $"{ds.OrgSlug}/{ds.Slug}";
            try
            {
                // Narrowed to the index role interface - reconciliation only reads/writes the
                // index, never build/meta/raster/analytics.
                IDdbIndex ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
                var summary = await ReconcileDatasetAsync(ds.OrgSlug, ds.InternalRef, ddb, WriteLine);

                WriteLine($"[{key}] unindexed={summary.Unindexed} reindexed={summary.Reindexed} " +
                          $"missing={summary.Missing} quarantineRemoved={summary.QuarantineRemoved} " +
                          $"durationMs={summary.DurationMs}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Index reconciliation failed for dataset '{Key}'", key);
                context?.WriteLine($"WARNING: reconciliation failed for '{key}': {ex.Message}");
            }
        }

        WriteLine("Index reconciliation sweep completed");
    }

    private async Task<ReconcileSummary> ReconcileDatasetAsync(string orgSlug, Guid internalRef, IDdbIndex ddb,
        Action<string> writeLine)
    {
        var sw = Stopwatch.StartNew();
        var root = ddb.DatasetFolderPath;

        // 1. Enumerate files on disk with the one dataset walker (DatasetPathPolicy.EnumerateFiles):
        //    the reserved folders are pruned during the walk (review round 2), so the live database
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
                await _indexQueue.EnqueueAsync(new DatasetKey(orgSlug, internalRef), toReindex);
                reindexed = toReindex.Length;
            }
            catch (Exception ex)
            {
                writeLine($"Failed to re-index {toReindex.Length} unindexed file(s): {ex.Message}");
            }
        }

        // 4. Indexed but missing on disk -> report only. Never destroy data automatically.
        if (missingPaths.Length > 0)
        {
            var sample = string.Join(", ", missingPaths.Take(10));
            writeLine($"{missingPaths.Length} indexed entries missing on disk (report only): {sample}" +
                      (missingPaths.Length > 10 ? ", …" : ""));
        }

        // 5. Age out old quarantined files.
        var quarantineRemoved = AgeOutQuarantine(root, writeLine);

        sw.Stop();
        return new ReconcileSummary(unindexedPaths.Length, reindexed, missingPaths.Length, quarantineRemoved,
            sw.ElapsedMilliseconds);
    }

    private int AgeOutQuarantine(string datasetRoot, Action<string> writeLine)
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
                writeLine($"Failed to remove aged quarantine file '{file}': {ex.Message}");
            }
        }

        return removed;
    }
}
