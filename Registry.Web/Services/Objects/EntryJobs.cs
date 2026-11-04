using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The background jobs of a dataset entry started by an object request: the rebuilds an entry needs after a move
/// or after a sidecar was removed (queued by <see cref="BuildScheduler"/>), and the mask-borders job.
/// Buildability and the live-build check come from libddb's build manifest; the jobs run as the current
/// user.
/// </summary>
public sealed class EntryJobs
{
    // Main files a .prj sidecar georeferences
    private static readonly HashSet<string> SidecarMainExtensions =
        new([".shp", ".dxf", ".dwg"], StringComparer.OrdinalIgnoreCase);

    private readonly IBuildArtifactResolver _artifacts;
    private readonly BuildScheduler _builds;
    private readonly IAuthManager _authManager;
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly IJobIndexQuery _jobIndex;
    private readonly ILogger<EntryJobs> _logger;

    public EntryJobs(IBuildArtifactResolver artifacts, BuildScheduler builds, IAuthManager authManager,
        IBackgroundJobsProcessor jobs, IJobIndexQuery jobIndex, ILogger<EntryJobs> logger)
    {
        _artifacts = artifacts;
        _builds = builds;
        _authManager = authManager;
        _jobs = jobs;
        _jobIndex = jobIndex;
        _logger = logger;
    }

    /// <summary>Queues a build of <paramref name="entry"/> when it is buildable and no build of it runs.</summary>
    /// <returns>Whether a build was queued.</returns>
    public async Task<bool> RebuildIfBuildableAsync(DatasetAccess access, string orgSlug, string dsSlug, Entry entry)
    {
        var build = _artifacts.GetBuild(access.Ddb, entry.Path);
        if (!build.CanBeBuilt() || build.HasLiveBuild())
            return false;

        await _builds.QueueAsync(BuildTarget.Of(access, orgSlug, dsSlug), entry.Path, entry.Hash);
        return true;
    }

    /// <summary>
    /// A .prj gives its shapefile or CAD drawing a coordinate system. Removing it makes DroneDB re-read the
    /// main file (local, without a CRS) and drop its stale vector artifacts, so the main file needs a rebuild
    /// now rather than at the next nightly completeness sweep. Failures are logged, never thrown.
    /// </summary>
    public async Task RebuildAfterSidecarRemovalAsync(DatasetAccess access, string orgSlug, string dsSlug,
        IEnumerable<string> deletedPaths)
    {
        var ddb = access.Ddb;
        var prjs = deletedPaths.Where(p => p.EndsWith(".prj", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (prjs.Length == 0) return;

        foreach (var prj in prjs)
        {
            EntrySummary[] mains;
            IReadOnlyList<EntryBuildArtifacts> builds;
            try
            {
                // Every casing of the main file's extension: one listing of the .prj's folder
                // (the native pattern ignores ASCII case), then one build manifest call
                var slash = prj.LastIndexOf('/');
                var folder = slash < 0 ? string.Empty : prj[..slash];
                var stem = Path.GetFileNameWithoutExtension(prj[(slash + 1)..]);
                mains = ddb.FindSummaries(new EntryQuery(folder, false, stem + ".*"))
                    .Where(e => Path.GetFileNameWithoutExtension(e.Path) == stem &&
                                SidecarMainExtensions.Contains(Path.GetExtension(e.Path)))
                    .ToArray();
                builds = mains.Length == 0 ? [] : _artifacts.GetBuilds(ddb, mains.Select(e => e.Path).ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cannot look up the main file of {Prj}", prj);
                continue;
            }

            for (var i = 0; i < mains.Length; i++)
            {
                var mainPath = mains[i].Path;
                try
                {
                    if (!builds[i].CanBeBuilt()) continue;

                    var jobId = await _builds.QueueAsync(BuildTarget.Of(access, orgSlug, dsSlug), mainPath,
                        mains[i].Hash);
                    _logger.LogInformation("Rebuilding {MainPath} after removing {Prj} (job {JobId})",
                        mainPath, prj, jobId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cannot enqueue the rebuild of {MainPath} after removing {Prj}",
                        mainPath, prj);
                }
            }
        }
    }

    /// <summary>Queues the mask-borders job of <paramref name="entry"/> into <paramref name="outputPath"/>.</summary>
    /// <returns>The job id.</returns>
    public async Task<string> EnqueueMaskBordersAsync(DatasetAccess access, string orgSlug, string dsSlug,
        Entry entry, string outputPath, int nearDist, bool white)
    {
        var user = await _authManager.GetCurrentUser();
        var meta = new IndexPayload(orgSlug, dsSlug, entry.Hash, user?.Id, null, entry.Path);
        return _jobs.EnqueueMaskBorders(access.Key, entry.Path, outputPath, nearDist, white, meta);
    }

    /// <summary>Refuses a second job of <paramref name="methodName"/> on <paramref name="path"/> while one waits or runs.</summary>
    /// <exception cref="ConflictException">One is queued, scheduled or running.</exception>
    public async Task EnsureNoActiveJobAsync(string orgSlug, string dsSlug, string path, string methodName,
        CancellationToken ct = default)
    {
        var jobs = await _jobIndex.GetByOrgDsAsync(orgSlug, dsSlug, 0, 1000, ct);
        var active = jobs.FirstOrDefault(j =>
            j.Path == path &&
            TaskStateCatalog.Active.Contains(j.CurrentState) &&
            j.MethodDisplay != null && j.MethodDisplay.Contains(methodName, StringComparison.Ordinal));

        if (active != null)
            throw new ConflictException($"A {methodName} operation is already in progress for '{path}'");
    }
}
