#nullable enable
using System;
using System.Collections.Generic;
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
using Registry.Web.Services.HeavyTasks;
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
                        $"skipped {ds_stats.SkippedNotBuildable + ds_stats.SkippedActive + ds_stats.SkippedComplete + ds_stats.SkippedPending + ds_stats.SkippedBackoff}, " +
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
            $"(not buildable: {totals.SkippedNotBuildable}, active: {totals.SkippedActive}, complete: {totals.SkippedComplete}, " +
            $"pending: {totals.SkippedPending}, backing off after failures: {totals.SkippedBackoff}), " +
            $"queued {totals.Queued} rebuild(s), {totals.Failed} dataset failure(s)");
    }

    /// <summary>
    /// Buildability and completeness of every entry with one native call, or null when DroneDB
    /// cannot answer in one go (the scan then asks entry by entry, isolating failures).
    /// </summary>
    private Dictionary<string, EntryBuildStatus>? GetBuildStatuses(IDDB ddb, IReadOnlyList<EntrySummary> entries,
        string key)
    {
        var paths = entries.Select(e => e.Path).Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
            return null;

        try
        {
            var statuses = ddb.GetBuildStatus(paths);
            if (statuses == null || statuses.Length != paths.Length)
                return null;

            var byPath = new Dictionary<string, EntryBuildStatus>(paths.Length, StringComparer.Ordinal);
            for (var i = 0; i < paths.Length; i++)
                byPath[paths[i]] = statuses[i];
            return byPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch build status failed for {Key}, checking entries one by one", key);
            return null;
        }
    }

    private async Task<DatasetStats> ScanDatasetAsync(IDDB ddb, string orgSlug, string dsSlug, string key)
    {
        var stats = new DatasetStats();
        var entries = ddb.FindSummaries(EntryQuery.All);
        var candidates = new List<EntrySummary>();
        var statuses = GetBuildStatuses(ddb, entries, key);

        foreach (var entry in entries)
        {
            stats.Scanned++;

            if (string.IsNullOrEmpty(entry.Path))
                continue;

            try
            {
                var status = statuses?.GetValueOrDefault(entry.Path);
                if (!(status?.Buildable ?? ddb.IsBuildable(entry.Path)))
                {
                    stats.SkippedNotBuildable++;
                    continue;
                }

                // Completeness first: the batch answers it, so only incomplete entries need a native call
                if (status?.Complete ?? ddb.IsBuildComplete(entry.Path))
                {
                    stats.SkippedComplete++;
                    continue;
                }

                if (ddb.IsBuildActive(entry.Path))
                {
                    stats.SkippedActive++;
                    continue;
                }

                candidates.Add(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to evaluate completeness for {Key} path={Path}",
                    key, entry.Path);
            }
        }

        if (candidates.Count == 0)
            return stats;

        // Pending builds wait for a dependency; adding it retries them (BuildPendingWrapper).
        var pending = GetPendingPaths(ddb, key);
        var history = await GetBuildHistoryAsync(orgSlug, dsSlug, candidates.Select(e => e.Path).ToArray());
        var now = DateTime.UtcNow;

        foreach (var entry in candidates)
        {
            if (pending.Contains(entry.Path))
            {
                stats.SkippedPending++;
                continue;
            }

            if (history.TryGetValue(entry.Path, out var jobs) &&
                ShouldBackOff(jobs, entry.Hash, now, out var failures, out var retryAt))
            {
                stats.SkippedBackoff++;
                _logger.LogDebug(
                    "Not re-enqueuing {Key} path={Path}: {Failures} failed build(s) of this content, next retry after {RetryAt:u}",
                    key, entry.Path, failures, retryAt);
                continue;
            }

            try
            {
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
                    "Failed to enqueue rebuild for {Key} path={Path}",
                    key, entry.Path);
            }
        }

        return stats;
    }

    // Failures that say nothing about the content: the build retries them itself
    // (AutomaticRetry) and they never count towards the back-off.
    private static readonly HashSet<string> TransientErrorTypes = new(StringComparer.Ordinal)
    {
        nameof(DdbBusyException), nameof(DdbBuildInProgressException), nameof(DdbRetryableException),
        nameof(DdbShutdownException)
    };

    private const int MaxBackoffDays = 30;

    /// <summary>
    /// Exponential back-off per entry content: after n consecutive failed builds of the
    /// same hash (latest first), the next nightly retry waits 2^(n-1) days, at most
    /// <see cref="MaxBackoffDays"/>. A build that keeps failing (an unreadable file)
    /// fades to a monthly attempt instead of failing every night. A transient failure
    /// (contention, disk full, tool timeout: libddb ABI 1.1 reports them as
    /// <see cref="DdbRetryableException"/>) never counts. A new upload (new hash)
    /// or any non-failed build (e.g. a manual retry that succeeded) resets the count.
    /// </summary>
    internal static bool ShouldBackOff(IReadOnlyList<BuildAttempt> jobsLatestFirst, string hash, DateTime now,
        out int failures, out DateTime retryAt)
    {
        failures = 0;
        retryAt = now;
        DateTime? lastFailure = null;

        foreach (var job in jobsLatestFirst)
        {
            if (job.Hash != hash) continue;
            if (job.State != "Failed") break;
            if (TransientErrorTypes.Contains(job.ErrorType ?? string.Empty)) continue;
            failures++;
            lastFailure ??= job.FailedAtUtc;
        }

        if (failures == 0 || lastFailure == null)
            return false;

        var days = Math.Min(MaxBackoffDays, 1 << Math.Min(failures - 1, 5));
        // Nightly runs drift by minutes: allow an hour of slack so "one day" means
        // the next night, not the one after.
        retryAt = lastFailure.Value.AddDays(days).AddHours(-1);
        return now < retryAt;
    }

    internal sealed record BuildAttempt(string Hash, string State, string? ErrorType, DateTime FailedAtUtc);

    private async Task<Dictionary<string, List<BuildAttempt>>> GetBuildHistoryAsync(string orgSlug, string dsSlug,
        IReadOnlyCollection<string> paths)
    {
        var result = new Dictionary<string, List<BuildAttempt>>();
        foreach (var chunk in paths.Distinct().Chunk(500))
        {
            var jobs = await _context.JobIndices
                .AsNoTracking()
                .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug && x.ToolId == HeavyToolIds.Build &&
                            chunk.Contains(x.Path!))
                .Select(x => new
                {
                    Path = x.Path!, x.Hash, x.CurrentState, x.ErrorType, x.CreatedAtUtc,
                    When = x.FailedAtUtc ?? x.LastStateChangeUtc ?? x.CreatedAtUtc
                })
                .ToArrayAsync();

            foreach (var group in jobs.GroupBy(j => j.Path))
                result[group.Key] = group
                    .OrderByDescending(j => j.CreatedAtUtc)
                    .Select(j => new BuildAttempt(j.Hash ?? string.Empty, j.CurrentState, j.ErrorType, j.When))
                    .ToList();
        }

        return result;
    }

    private HashSet<string> GetPendingPaths(IDDB ddb, string key)
    {
        try
        {
            return ddb.GetPendingBuildInfo().Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot read pending builds for {Key}", key);
            return [];
        }
    }

    private sealed class DatasetStats
    {
        public int Scanned;
        public int SkippedNotBuildable;
        public int SkippedActive;
        public int SkippedComplete;
        public int SkippedPending;
        public int SkippedBackoff;
        public int Queued;
    }

    private sealed class Totals
    {
        public int Scanned;
        public int SkippedNotBuildable;
        public int SkippedActive;
        public int SkippedComplete;
        public int SkippedPending;
        public int SkippedBackoff;
        public int Queued;
        public int Failed;

        public void Add(DatasetStats s)
        {
            Scanned += s.Scanned;
            SkippedNotBuildable += s.SkippedNotBuildable;
            SkippedActive += s.SkippedActive;
            SkippedComplete += s.SkippedComplete;
            SkippedPending += s.SkippedPending;
            SkippedBackoff += s.SkippedBackoff;
            Queued += s.Queued;
        }
    }
}
