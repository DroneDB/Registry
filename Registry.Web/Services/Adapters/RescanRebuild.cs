using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>Outcome of <see cref="RescanRebuild.RunAsync"/>.</summary>
/// <param name="WholeDataset">The whole dataset was rebuilt (unfiltered rescan).</param>
/// <param name="Queued">Rescanned entries whose rebuild was queued (filtered rescan).</param>
/// <param name="Failed">Rescanned entries whose rebuild could not be queued (filtered rescan).</param>
public sealed record RescanRebuildResult(bool WholeDataset, int Queued, int Failed);

/// <summary>
/// What a rescan rebuilds, for every rescan path; the <see cref="IRescanBuildRunner"/> queues the builds, so the rescan
/// does not wait for them and a build that fails does not fail the rescan. An unfiltered rescan re-reads every
/// entry, so it clears the build cache and queues the rebuild of the dataset. A filtered rescan
/// queues the rebuilds of the rescanned entries that are buildable only: the rest of the dataset keeps
/// its artifacts. An entry whose rebuild cannot be queued does not stop the others; the artifact
/// completeness check queues it again. The upgrade rescans (<see cref="Initialization.UpgradeRescanStartupTask"/>) rebuild only
/// the entries whose build key changed (<see cref="RescanResult.BuildKeyChanged"/>, D-020): the rest keep their builds.
/// </summary>
public sealed class RescanRebuild
{
    private readonly IDatasetCacheInvalidator _caches;
    private readonly RegistryContext _context;
    private readonly BuildScheduler _scheduler;
    private readonly ILogger<RescanRebuild> _logger;

    public RescanRebuild(IDatasetCacheInvalidator caches, RegistryContext context, BuildScheduler scheduler,
        ILogger<RescanRebuild> logger)
    {
        _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <param name="onProgress">Called with the fraction of the entry rebuilds queued (filtered rescan only).</param>
    /// <param name="onlyChangedKeys">
    /// Rebuild only the rescanned entries whose build key changed (the rescan of a filtered scope only: an
    /// unfiltered rescan rebuilds the dataset).
    /// </param>
    public async Task<RescanRebuildResult> RunAsync(BuildTarget target, RescanFilter filter,
        IReadOnlyCollection<RescanResult> results, IRescanBuildRunner builds, Action<double>? onProgress = null,
        bool onlyChangedKeys = false, CancellationToken ct = default)
    {
        var (ddb, orgSlug, dsSlug) = (target.Ddb, target.OrgSlug, target.DsSlug);
        if (filter.IsEmpty)
        {
            _logger.LogInformation("Clearing the build cache of {OrgSlug}/{DsSlug} and queuing its rebuild", orgSlug, dsSlug);
            ddb.ClearBuildCache();
            await _caches.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);
            await QueueClearedRebuildAsync(target, builds);
            return new RescanRebuildResult(true, 0, 0);
        }

        if (results.Count > 0)
            await _caches.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);

        var entries = BuildableEntries(ddb, onlyChangedKeys ? results.Where(r => r.BuildKeyChanged) : results);
        _logger.LogInformation("Queuing the rebuilds of {Count} rescanned entries of {OrgSlug}/{DsSlug}",
            entries.Count, orgSlug, dsSlug);

        int queued = 0, failed = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await builds.BuildEntry(entries[i]);
                queued++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Cannot queue the rebuild of rescanned entry {Path} of {OrgSlug}/{DsSlug}",
                    entries[i].Path, orgSlug, dsSlug);
            }

            onProgress?.Invoke((double)(i + 1) / entries.Count);
        }

        await builds.BuildPending();
        return new RescanRebuildResult(false, queued, failed);
    }

    // From the clear of the build cache until its rebuild ends, the dataset has no derived files. When the rebuild of
    // the dataset cannot be queued, the builds of the entries are queued as their upload queued them, so the dataset
    // gets them back without waiting for the completeness check; a deleted dataset gets nothing. The failure itself is
    // the outcome of the rescan: it goes on to the caller
    private async Task QueueClearedRebuildAsync(BuildTarget target, IRescanBuildRunner builds)
    {
        var queued = false;
        try
        {
            await builds.BuildDataset();
            queued = true;
        }
        finally
        {
            if (!queued)
                await BestEffort.RunAsync(() => QueueEntryBuildsAsync(target),
                    ex => _logger.LogError(ex, "Cannot queue the builds of {OrgSlug}/{DsSlug} after its rebuild was " +
                                               "not queued: the artifact completeness check queues them", target.OrgSlug,
                        target.DsSlug), CancellationToken.None);
        }

        await builds.BuildPending();
    }

    private async Task QueueEntryBuildsAsync(BuildTarget target)
    {
        if (!await _context.Datasets.AsNoTracking()
                .AnyAsync(d => d.InternalRef == target.Dataset.InternalRef, CancellationToken.None))
            return;

        var paths = target.Ddb.FindSummaries(EntryQuery.All)
            .Where(e => e.Type != EntryType.Directory)
            .Select(e => e.Path)
            .ToArray();
        var queued = await _scheduler.QueueAddedAsync(target, paths, BuildOrigin.Service, CancellationToken.None);
        _logger.LogWarning("The rebuild of {OrgSlug}/{DsSlug} was not queued after its build cache was cleared: " +
                           "{Count} build(s) queued", target.OrgSlug, target.DsSlug, queued.Count);
    }

    private List<RescanResult> BuildableEntries(IDDB ddb, IEnumerable<RescanResult> results)
    {
        var candidates = results.Where(r => r.Success && !string.IsNullOrEmpty(r.Path))
            .DistinctBy(r => r.Path, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return [];

        try
        {
            // One native call for every entry (the build manifest); builds come back in input order
            var builds = ddb.GetBuildArtifacts(candidates.Select(r => r.Path).ToArray());
            return candidates.Where((_, i) => i < builds.Count && builds[i].CanBeBuilt()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot tell which of {Count} rescanned entries are buildable", candidates.Count);
            return [];
        }
    }
}
