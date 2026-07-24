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
/// </summary>
public class BuildStatusService : IBuildStatusService
{
    // Only entries of these types can ever be buildable (mirrors
    // ddb::isBuildableInternal in DroneDB/src/library/build.cpp). Filtering on
    // this first avoids an expensive native IsBuildable/IsBuildComplete
    // round-trip for the common case of images, videos, markdown, etc.
    private static readonly EntryType[] PotentiallyBuildableTypes =
    [
        EntryType.PointCloud, EntryType.GeoRaster, EntryType.Model,
        EntryType.Vector, EntryType.GaussianSplat, EntryType.Tiles3D
    ];

    private const string BuildToolId = "build";

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

        var jobs = await _jobIndexQuery.GetByOrgDsAsync(orgSlug, dsSlug, 0, int.MaxValue);
        var buildJobs = jobs.Where(j => j.ToolId == BuildToolId && j.Path != null).ToArray();

        var activePaths = buildJobs
            .Where(j => TaskStateCatalog.Active.Contains(j.CurrentState))
            .Select(j => j.Path!)
            .ToHashSet();

        var latestJobByPath = buildJobs
            .GroupBy(j => j.Path!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAtUtc).First());

        var pendingByPath = ddb.GetPendingBuildInfo().ToDictionary(p => p.Path, p => p);

        foreach (var entry in candidates)
            AnnotateEntry(ddb, entry, activePaths, latestJobByPath, pendingByPath);
    }

    private void AnnotateEntry(
        IDDB ddb,
        EntryDto entry,
        HashSet<string> activePaths,
        IReadOnlyDictionary<string, JobIndex> latestJobByPath,
        IReadOnlyDictionary<string, PendingBuildInfo> pendingByPath)
    {
        if (!IsBuildableSafe(ddb, entry.Path))
            return;

        if (activePaths.Contains(entry.Path))
        {
            entry.BuildStatus = "building";
            return;
        }

        if (pendingByPath.TryGetValue(entry.Path, out var pending))
        {
            entry.BuildStatus = "pending";
            entry.BuildMissingDependencies = pending.MissingDependencies;
            return;
        }

        if (IsBuildCompleteSafe(ddb, entry.Path))
            return; // ready: the default state, nothing to transmit

        if (latestJobByPath.TryGetValue(entry.Path, out var latestJob) && latestJob.CurrentState == "Failed")
        {
            entry.BuildStatus = "failed";
            return;
        }

        entry.BuildStatus = "queued";
    }

    private bool IsBuildableSafe(IDDB ddb, string path)
    {
        try
        {
            return ddb.IsBuildable(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot determine buildability for '{Path}'", path);
            return false;
        }
    }

    private bool IsBuildCompleteSafe(IDDB ddb, string path)
    {
        try
        {
            return ddb.IsBuildComplete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot determine build completeness for '{Path}'", path);
            return false;
        }
    }
}
