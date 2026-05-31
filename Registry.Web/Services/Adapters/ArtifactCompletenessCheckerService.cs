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
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Recurring service that scans every entry in every dataset and enqueues a
/// rebuild for any buildable entry whose build output is missing or empty
/// (e.g. after a build-format migration like FGB→MVT or EPT→COPC).
///
/// Authoritative completeness logic lives in the DroneDB C++ library
/// (<c>DDBIsBuildComplete</c>): the service is intentionally thin so that
/// .NET cannot drift from the native layout rules.
/// </summary>
public class ArtifactCompletenessCheckerService
{
    // Small pacing delay between entries; keeps the scan effectively
    // single-threaded so it never competes with user-driven builds.
    private const int InterEntryDelayMs = 50;

    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _backgroundJob;
    private readonly ILogger<ArtifactCompletenessCheckerService> _logger;

    public ArtifactCompletenessCheckerService(
        RegistryContext context,
        IDdbManager ddbManager,
        IBackgroundJobsProcessor backgroundJob,
        ILogger<ArtifactCompletenessCheckerService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _backgroundJob = backgroundJob ?? throw new ArgumentNullException(nameof(backgroundJob));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 0)]
    public async Task CheckAndQueueAsync(PerformContext? context = null)
    {
        void WriteLine(string message)
        {
            _logger.LogInformation(message);
            context?.WriteLine(message);
        }

        WriteLine("Starting artifact completeness check across all datasets");

        var datasets = await _context.Datasets
            .AsNoTracking()
            .Include(d => d.Organization)
            .Select(d => new { d.Slug, OrgSlug = d.Organization.Slug, d.InternalRef })
            .ToArrayAsync();

        var totals = new Totals();

        foreach (var ds in datasets)
        {
            var key = $"{ds.OrgSlug}/{ds.Slug}";
            try
            {
                var ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
                var ds_stats = await ScanDatasetAsync(ddb, ds.OrgSlug, ds.Slug, key);
                totals.Add(ds_stats);

                if (ds_stats.Queued > 0)
                    WriteLine(
                        $"{key}: scanned {ds_stats.Scanned}, " +
                        $"skipped {ds_stats.SkippedNotBuildable + ds_stats.SkippedActive + ds_stats.SkippedComplete}, " +
                        $"queued {ds_stats.Queued} rebuild(s)");
            }
            catch (Exception ex)
            {
                totals.Failed++;
                _logger.LogWarning(ex, "Artifact completeness check failed for {Key}", key);
                context?.WriteLine($"WARNING: completeness check failed for {key}: {ex.Message}");
            }
        }

        WriteLine(
            $"Artifact completeness check completed: {datasets.Length - totals.Failed}/{datasets.Length} datasets, " +
            $"scanned {totals.Scanned} entries " +
            $"(not buildable: {totals.SkippedNotBuildable}, active: {totals.SkippedActive}, complete: {totals.SkippedComplete}), " +
            $"queued {totals.Queued} rebuild(s), {totals.Failed} dataset failure(s)");
    }

    private async Task<DatasetStats> ScanDatasetAsync(IDDB ddb, string orgSlug, string dsSlug, string key)
    {
        var stats = new DatasetStats();
        var entries = ddb.Search(string.Empty, recursive: true);

        foreach (var entry in entries)
        {
            stats.Scanned++;

            if (string.IsNullOrEmpty(entry.Path))
                continue;

            try
            {
                if (!ddb.IsBuildable(entry.Path))
                {
                    stats.SkippedNotBuildable++;
                    continue;
                }

                if (ddb.IsBuildActive(entry.Path))
                {
                    stats.SkippedActive++;
                    continue;
                }

                if (ddb.IsBuildComplete(entry.Path))
                {
                    stats.SkippedComplete++;
                    continue;
                }

                var meta = new IndexPayload(
                    orgSlug,
                    dsSlug,
                    entry.Hash,
                    MagicStrings.AutoBuildServiceUserId,
                    null,
                    entry.Path);

                _backgroundJob.EnqueueIndexed(
                    () => HangfireUtils.BuildWrapper(ddb, entry.Path, false, null),
                    meta);

                stats.Queued++;
                _logger.LogInformation(
                    "Enqueued rebuild for {Key} path={Path} (incomplete artifacts)",
                    key, entry.Path);

                await Task.Delay(InterEntryDelayMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to evaluate completeness for {Key} path={Path}",
                    key, entry.Path);
            }
        }

        return stats;
    }

    private sealed class DatasetStats
    {
        public int Scanned;
        public int SkippedNotBuildable;
        public int SkippedActive;
        public int SkippedComplete;
        public int Queued;
    }

    private sealed class Totals
    {
        public int Scanned;
        public int SkippedNotBuildable;
        public int SkippedActive;
        public int SkippedComplete;
        public int Queued;
        public int Failed;

        public void Add(DatasetStats s)
        {
            Scanned += s.Scanned;
            SkippedNotBuildable += s.SkippedNotBuildable;
            SkippedActive += s.SkippedActive;
            SkippedComplete += s.SkippedComplete;
            Queued += s.Queued;
        }
    }
}
