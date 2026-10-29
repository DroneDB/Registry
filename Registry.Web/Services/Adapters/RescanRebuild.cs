using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>Outcome of <see cref="RescanRebuild.RunAsync"/>.</summary>
/// <param name="WholeDataset">The whole dataset was rebuilt (unfiltered rescan).</param>
/// <param name="Built">Rescanned entries rebuilt (filtered rescan).</param>
/// <param name="Failed">Rescanned entries whose rebuild failed (filtered rescan).</param>
public sealed record RescanRebuildResult(bool WholeDataset, int Built, int Failed);

/// <summary>
/// What a rescan rebuilds, for every rescan path. An unfiltered rescan re-reads every
/// entry, so it clears the build cache and rebuilds the dataset. A filtered rescan
/// rebuilds only the rescanned entries that are buildable: the rest of the dataset keeps
/// its artifacts. A failed entry rebuild does not stop the others; the artifact
/// completeness check enqueues it again.
/// </summary>
public sealed class RescanRebuild
{
    private readonly IDatasetCacheInvalidator _caches;
    private readonly ILogger<RescanRebuild> _logger;

    public RescanRebuild(IDatasetCacheInvalidator caches, ILogger<RescanRebuild> logger)
    {
        _caches = caches ?? throw new ArgumentNullException(nameof(caches));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <param name="onProgress">Called with the fraction of the entry rebuilds done (filtered rescan only).</param>
    public async Task<RescanRebuildResult> RunAsync(string orgSlug, string dsSlug, IDDB ddb, RescanFilter filter,
        IReadOnlyCollection<RescanResult> results, IRescanBuildRunner builds, Action<double>? onProgress = null,
        CancellationToken ct = default)
    {
        if (filter.IsEmpty)
        {
            _logger.LogInformation("Clearing build cache and rebuilding {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
            ddb.ClearBuildCache();
            await _caches.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);
            await builds.BuildDataset();
            await builds.BuildPending();
            return new RescanRebuildResult(true, 0, 0);
        }

        if (results.Count > 0)
            await _caches.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);

        var entries = BuildableEntries(ddb, results);
        _logger.LogInformation("Rebuilding {Count} rescanned entries of {OrgSlug}/{DsSlug}",
            entries.Count, orgSlug, dsSlug);

        int built = 0, failed = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await builds.BuildEntry(entries[i]);
                built++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Rebuild of rescanned entry {Path} of {OrgSlug}/{DsSlug} failed",
                    entries[i].Path, orgSlug, dsSlug);
            }

            onProgress?.Invoke((double)(i + 1) / entries.Count);
        }

        await builds.BuildPending();
        return new RescanRebuildResult(false, built, failed);
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
