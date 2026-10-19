#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IBuildStatusService"/> implementation. Combines the
/// Hangfire job index (for "building"/"failed") with the DroneDB C++
/// library's pending-build view (for "pending", including the missing
/// dependency names) and build-completeness check (for the default "ready"
/// state) into a single per-entry status.
/// A listing costs at most two native calls (batch build status, pending
/// builds) and two job index queries scoped to the listed paths, whatever
/// the number of entries.
/// </summary>
public class BuildStatusService : IBuildStatusService
{
    // Only entries of these types can ever be buildable (mirrors
    // ddb::isBuildableInternal in DroneDB/src/library/build.cpp). Filtering on
    // this first keeps images, videos, markdown, etc. out of the native call.
    private static readonly EntryType[] PotentiallyBuildableTypes =
    [
        EntryType.PointCloud, EntryType.GeoRaster, EntryType.Model,
        EntryType.Vector, EntryType.GaussianSplat, EntryType.Tiles3D
    ];

    private const string BuildToolId = HeavyToolIds.Build;

    private readonly IJobIndexQuery _jobIndexQuery;
    private readonly ILogger<BuildStatusService> _logger;

    public BuildStatusService(IJobIndexQuery jobIndexQuery, ILogger<BuildStatusService> logger)
    {
        _jobIndexQuery = jobIndexQuery;
        _logger = logger;
    }

    public async Task AnnotateAsync(string orgSlug, string dsSlug, IDDB ddb, IReadOnlyList<EntryDto> entries)
    {
        var candidates = entries.Where(e => PotentiallyBuildableTypes.Contains(e.Type)).ToList();
        if (candidates.Count == 0)
            return;

        var statusByPath = GetBuildStatusSafe(ddb, candidates);
        if (statusByPath == null)
            return;

        var buildable = candidates
            .Where(e => statusByPath.TryGetValue(e.Path, out var status) && status.Buildable)
            .ToList();
        if (buildable.Count == 0)
            return;

        var activePaths = await _jobIndexQuery.GetActivePathsAsync(orgSlug, dsSlug, BuildToolId,
            buildable.Select(e => e.Path).ToArray());

        var notBuilding = buildable.Where(e => !activePaths.Contains(e.Path)).ToList();
        foreach (var entry in buildable.Except(notBuilding))
            entry.BuildStatus = "building";

        if (notBuilding.Count == 0)
            return;

        var pendingByPath = ddb.GetPendingBuildInfo()
            .GroupBy(p => p.Path)
            .ToDictionary(g => g.Key, g => g.First());

        // "failed" or "queued": decided by the latest build job, read only for these entries
        var unfinished = new List<EntryDto>();

        foreach (var entry in notBuilding)
        {
            if (pendingByPath.TryGetValue(entry.Path, out var pending))
            {
                entry.BuildStatus = "pending";
                entry.BuildMissingDependencies = pending.MissingDependencies;
            }
            else if (!statusByPath[entry.Path].Complete)
                unfinished.Add(entry);
            // else ready: the default state, nothing to transmit
        }

        if (unfinished.Count == 0)
            return;

        var latestStateByPath = await _jobIndexQuery.GetLatestStatesByPathAsync(orgSlug, dsSlug, BuildToolId,
            unfinished.Select(e => e.Path).ToArray());

        foreach (var entry in unfinished)
            entry.BuildStatus = latestStateByPath.TryGetValue(entry.Path, out var state) && state == "Failed"
                ? "failed"
                : "queued";
    }

    private Dictionary<string, EntryBuildStatus>? GetBuildStatusSafe(IDDB ddb, IReadOnlyList<EntryDto> candidates)
    {
        try
        {
            return ddb.GetBuildStatus(candidates.Select(e => e.Path).Distinct().ToArray())
                .GroupBy(s => s.Path)
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot determine the build status of {Count} entries", candidates.Count);
            return null;
        }
    }
}
