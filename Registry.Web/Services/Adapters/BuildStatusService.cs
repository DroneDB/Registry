using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IBuildStatusService"/> implementation. Combines the
/// Hangfire job index (for "building"/"failed") with libddb's build manifest
/// (buildability, the pending marker with the missing dependency names and whether it awaits them,
/// and completeness for the default "ready" state) into a single per-entry status,
/// and lists the artifacts that exist (<see cref="EntryDto.Artifacts"/>).
/// A listing costs one native call (the manifest of the listed entries) and two
/// job index queries scoped to the listed paths, whatever the number of entries. A background rebuild that has not
/// started processing gives no status (see <see cref="AnnotateAsync"/>).
/// </summary>
public class BuildStatusService : IBuildStatusService
{
    private const string BuildToolId = HeavyToolIds.Build;

    private readonly IJobIndexQuery _jobIndexQuery;
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IUrlBuilder _urls;
    private readonly ILogger<BuildStatusService> _logger;

    public BuildStatusService(IJobIndexQuery jobIndexQuery, IBuildArtifactResolver artifacts, IUrlBuilder urls,
        ILogger<BuildStatusService> logger)
    {
        _jobIndexQuery = jobIndexQuery;
        _artifacts = artifacts;
        _urls = urls;
        _logger = logger;
    }

    public async Task AnnotateAsync(string orgSlug, string dsSlug, IDDB ddb, IReadOnlyList<EntryDto> entries)
    {
        // Only the types libddb builds (DDBGetEntryTypes) can have a build: images, videos, markdown, ... make
        // no native call
        var candidates = entries.Where(e => _artifacts.IsBuildableType(e.Type)).ToList();
        if (candidates.Count == 0)
            return;

        var buildByPath = GetBuildsSafe(ddb, candidates);
        if (buildByPath == null)
            return;

        // The artifacts that exist, from the same manifest call
        foreach (var entry in candidates)
        {
            if (buildByPath.TryGetValue(entry.Path, out var build) &&
                _urls.ArtifactLinks(orgSlug, dsSlug, build) is { Count: > 0 } links)
                entry.Artifacts = [.. links];
        }

        var buildable = candidates
            .Where(e => buildByPath.TryGetValue(e.Path, out var build) && build.CanBeBuilt())
            .ToList();
        if (buildable.Count == 0)
            return;

        // A background rebuild that only waits for its turn is not a build in progress: the entry is usable and is
        // judged as if no job were active (and it is not "queued" either: nothing waits for it)
        var activeJobs = await _jobIndexQuery.GetActiveJobsAsync(orgSlug, dsSlug, BuildToolId,
            buildable.Select(e => e.Path).ToArray());
        var buildingPaths = activeJobs.Where(j => !IsWaitingBackgroundRebuild(j)).Select(j => j.Path).ToHashSet();
        var waitingPaths = activeJobs.Select(j => j.Path).Except(buildingPaths).ToHashSet();

        var notBuilding = buildable.Where(e => !buildingPaths.Contains(e.Path)).ToList();
        foreach (var entry in buildable.Except(notBuilding))
            entry.BuildStatus = "building";

        if (notBuilding.Count == 0)
            return;

        // "failed" or "queued": decided by the latest build job, read only for these entries
        var unfinished = new List<EntryDto>();

        foreach (var entry in notBuilding)
        {
            var build = buildByPath[entry.Path].Build!;
            if (HasPendingMarker(build))
            {
                entry.BuildStatus = "pending";
                entry.BuildMissingDependencies = [.. build.MissingDependencies];
                entry.BuildAwaitingFiles = build.AwaitingFiles;
            }
            else if (build.State != BuildState.Complete && !waitingPaths.Contains(entry.Path))
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

    // The background rebuild of an entry made with an older layout (the rebuilds queue serves one at a time, so it
    // can wait for days) that has not started: an entry that opens fine has no build to show
    private static bool IsWaitingBackgroundRebuild(ActiveJob job) =>
        job.State != ProcessingState.StateName && DdbJobDescriptor.IsBackgroundRebuildDisplay(job.MethodDisplay);

    // A pending marker: libddb reports "pending", or "building" (the lock wins) with the marker's
    // missing dependencies while a build that was deferred runs again outside a build job
    private static bool HasPendingMarker(EntryBuild build) =>
        build.State == BuildState.Pending ||
        (build.State == BuildState.Building && build.MissingDependencies.Count > 0);

    private Dictionary<string, EntryBuildArtifacts>? GetBuildsSafe(IDDB ddb, List<EntryDto> candidates)
    {
        try
        {
            return _artifacts.GetBuilds(ddb, candidates.Select(e => e.Path).Distinct().ToArray())
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
