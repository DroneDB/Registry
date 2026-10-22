#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Services.Builds;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Recurring service that scans every entry in every dataset and enqueues a
/// rebuild for any buildable entry whose build output is missing or empty
/// (e.g. after a build-format migration like FGB→MVT or EPT→COPC).
///
/// Authoritative buildability, state and completeness come from the DroneDB C++
/// library's build manifest (<c>DDBGetBuildArtifacts</c>, one call per dataset): the
/// service is intentionally thin so that .NET cannot drift from the native layout rules.
/// </summary>
/// <remarks>
/// One run at a time across the hosts (the nightly run and the admin trigger of
/// <c>check-artifact-completeness</c>): two would queue the same rebuilds. Timeout 0: the run in
/// progress does the work.
/// </remarks>
[AutomaticRetry(Attempts = 0)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class ArtifactCompletenessCheckerService
{
    // Small pacing delay between entries; keeps the scan effectively
    // single-threaded so it never competes with user-driven builds.
    private const int InterEntryDelayMs = 50;

    private readonly RegistryContext _context;
    private readonly DatasetSweep _sweep;
    private readonly IDdbManager _ddbManager;
    private readonly IBuildScheduler _builds;
    private readonly ILogger<ArtifactCompletenessCheckerService> _logger;

    public ArtifactCompletenessCheckerService(
        RegistryContext context,
        DatasetSweep sweep,
        IDdbManager ddbManager,
        IBuildScheduler builds,
        ILogger<ArtifactCompletenessCheckerService> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _sweep = sweep ?? throw new ArgumentNullException(nameof(sweep));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _builds = builds ?? throw new ArgumentNullException(nameof(builds));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The job as stored before RQ-09 (D4): runs with the context's shutdown token.</summary>
    public Task CheckAndQueueAsync(PerformContext? context) => CheckAndQueueAsync(context, JobCancellation.Of(context));

    public async Task CheckAndQueueAsync(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);
        console.Info("Starting artifact completeness check across all datasets");

        var totals = new Totals();

        var result = await _sweep.ForEachAsync(console, "Completeness check", async (ds, ct) =>
        {
            var ddb = _ddbManager.Get(ds.OrgSlug, ds.InternalRef);
            var stats = await ScanDatasetAsync(ddb, new DatasetKey(ds.OrgSlug, ds.InternalRef), ds.DsSlug, ds.Key, ct);
            totals.Add(stats);

            if (stats.Queued > 0)
                console.Info("{Dataset}: scanned {Scanned}, skipped {Skipped}, queued {Queued} rebuild(s)",
                    ds.Key, stats.Scanned,
                    stats.SkippedNotBuildable + stats.SkippedActive + stats.SkippedComplete + stats.SkippedPending +
                    stats.SkippedBackoff,
                    stats.Queued);
        }, cancellationToken);

        console.Info("Artifact completeness check completed: {Succeeded}/{Datasets} datasets, scanned {Scanned} entries " +
                     "(not buildable: {NotBuildable}, active: {Active}, complete: {Complete}, pending: {Pending}, " +
                     "backing off after failures: {Backoff}), queued {Queued} rebuild(s), {Failed} dataset failure(s)",
            result.Succeeded, result.Datasets, totals.Scanned, totals.SkippedNotBuildable, totals.SkippedActive,
            totals.SkippedComplete, totals.SkippedPending, totals.SkippedBackoff, totals.Queued, result.Failed);
    }

    /// <summary>
    /// The build manifest of every entry with one native call (XI-04), or null when DroneDB cannot
    /// answer in one go (the scan then asks entry by entry, isolating failures).
    /// </summary>
    private Dictionary<string, EntryBuildArtifacts>? GetBuilds(IDDB ddb, IReadOnlyList<EntrySummary> entries,
        string key)
    {
        var paths = entries.Select(e => e.Path).Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
            return null;

        try
        {
            var builds = ddb.GetBuildArtifacts(paths);
            if (builds == null || builds.Count != paths.Length)
                return null;

            var byPath = new Dictionary<string, EntryBuildArtifacts>(paths.Length, StringComparer.Ordinal);
            for (var i = 0; i < paths.Length; i++)
                byPath[paths[i]] = builds[i];
            return byPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch build manifest failed for {Key}, checking entries one by one", key);
            return null;
        }
    }

    private async Task<DatasetStats> ScanDatasetAsync(IDDB ddb, DatasetKey dataset, string dsSlug, string key,
        CancellationToken ct)
    {
        var orgSlug = dataset.OrgSlug;
        var stats = new DatasetStats();
        var entries = ddb.FindSummaries(EntryQuery.All);
        var candidates = new List<EntrySummary>();
        var builds = GetBuilds(ddb, entries, key);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            stats.Scanned++;

            if (string.IsNullOrEmpty(entry.Path))
                continue;

            try
            {
                var build = (builds?.GetValueOrDefault(entry.Path) ?? ddb.GetBuildArtifacts([entry.Path])[0]).Build;
                if (build is not { Buildable: true })
                {
                    stats.SkippedNotBuildable++;
                    continue;
                }

                // libddb's precedence: a live build, then a pending marker (it waits for a
                // dependency; adding it retries the build), then complete artifacts
                switch (build.State)
                {
                    case BuildState.Building:
                        stats.SkippedActive++;
                        continue;
                    case BuildState.Pending:
                        stats.SkippedPending++;
                        continue;
                    case BuildState.Complete:
                        stats.SkippedComplete++;
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

        var history = await GetBuildHistoryAsync(orgSlug, dsSlug, candidates.Select(e => e.Path).ToArray(), ct);
        var now = DateTime.UtcNow;

        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
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
                await _builds.QueueAsync(new BuildTarget(ddb, dataset, orgSlug, dsSlug), entry.Path, entry.Hash,
                    origin: BuildOrigin.Service, ct: ct);

                stats.Queued++;
                _logger.LogInformation(
                    "Enqueued rebuild for {Key} path={Path} (incomplete artifacts)",
                    key, entry.Path);

                await Task.Delay(InterEntryDelayMs, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        IReadOnlyCollection<string> paths, CancellationToken ct)
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
                .ToArrayAsync(ct);

            foreach (var group in jobs.GroupBy(j => j.Path))
                result[group.Key] = group
                    .OrderByDescending(j => j.CreatedAtUtc)
                    .Select(j => new BuildAttempt(j.Hash ?? string.Empty, j.CurrentState, j.ErrorType, j.When))
                    .ToList();
        }

        return result;
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
