#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Builds;

/// <summary>
/// The one place that queues builds of a dataset: whether an entry is buildable and whether a dataset has
/// deferred builds comes from libddb (the build manifest); the scheduler only queues. Before the first
/// job of a call is queued, the dataset is flagged as having pending builds, so a build that fails at once is
/// retried by the pending-build coordinator (the flag must precede the job, or a quick failure is missed).
/// </summary>
public sealed class BuildScheduler
{
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly IPendingBuildFlag _pending;
    private readonly IBuildRequester _requester;
    private readonly ILogger<BuildScheduler> _logger;

    public BuildScheduler(IBuildArtifactResolver artifacts, IBackgroundJobsProcessor jobs, IPendingBuildFlag pending,
        IBuildRequester requester, ILogger<BuildScheduler> logger)
    {
        _artifacts = artifacts;
        _jobs = jobs;
        _pending = pending;
        _requester = requester;
        _logger = logger;
    }

    /// <summary>
    /// Queues a build of each of <paramref name="paths"/> libddb reports buildable (one manifest call), in order.
    /// </summary>
    /// <returns>The builds queued (the buildable paths).</returns>
    public async Task<IReadOnlyList<QueuedBuild>> QueueBuildableAsync(BuildTarget target, IReadOnlyList<string> paths,
        BuildOrigin? origin = null, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return [];

        // One build object per path, in input order (the build manifest)
        var buildable = _artifacts.GetBuilds(target.Ddb, paths)
            .Where(b => b.CanBeBuilt())
            .ToArray();
        if (buildable.Length == 0)
            return [];

        var userId = await UserIdAsync(origin);
        await FlagPendingAsync(target);

        var queued = new List<QueuedBuild>(buildable.Length);
        foreach (var build in buildable)
        {
            ct.ThrowIfCancellationRequested();
            var jobId = _jobs.EnqueueBuild(target.Dataset, build.Path, false,
                Payload(target, userId, origin, build.Path, build.Hash));
            queued.Add(new QueuedBuild(build.Path, build.Hash, jobId));
        }

        _logger.LogInformation("Queued {Count} build(s) in {Org}/{Ds}", queued.Count, target.OrgSlug, target.DsSlug);
        return queued;
    }

    /// <summary>
    /// Queues the build of <paramref name="path"/> the caller decided on (its buildability and live-build checks
    /// are the caller's, as for the build endpoint).
    /// </summary>
    /// <returns>The job id.</returns>
    public Task<string> QueueAsync(BuildTarget target, string path, string? hash, bool force = false,
        BuildOrigin? origin = null) =>
        EnqueueAsync(target, origin, path, hash, payload => _jobs.EnqueueBuild(target.Dataset, path, force, payload));

    /// <summary>
    /// Queues the build of <paramref name="path"/> on the background queue, for an entry nobody waits for (the check
    /// for entries of an older layout): the same bookkeeping as <see cref="QueueAsync"/>, another queue and a
    /// capped number of threads.
    /// </summary>
    /// <returns>The job id.</returns>
    public Task<string> QueueRebuildAsync(BuildTarget target, string path, string? hash, BuildOrigin? origin = null) =>
        EnqueueAsync(target, origin, path, hash, payload => _jobs.EnqueueRebuild(target.Dataset, path, payload));

    /// <summary>Queues a build of the whole dataset (an import, a rescan).</summary>
    /// <returns>The job id.</returns>
    public Task<string> QueueDatasetAsync(BuildTarget target, bool force, BuildOrigin? origin = null) =>
        EnqueueAsync(target, origin, null, null, payload => _jobs.EnqueueBuild(target.Dataset, null, force, payload));

    /// <summary>Queues the pending-build job when libddb reports deferred builds in the dataset.</summary>
    /// <returns>The job id, or null when nothing is deferred.</returns>
    public async Task<string?> QueuePendingAsync(BuildTarget target, BuildOrigin? origin = null)
    {
        if (!target.Ddb.IsBuildPending())
            return null;

        var userId = await UserIdAsync(origin);
        var jobId = _jobs.EnqueueBuildPending(target.Dataset, Payload(target, userId, origin, null, null));
        _logger.LogInformation("Deferred builds in {Org}/{Ds}: pending-build job {JobId}", target.OrgSlug,
            target.DsSlug, jobId);
        return jobId;
    }

    private async Task<string?> UserIdAsync(BuildOrigin? origin) =>
        origin != null ? origin.UserId : await _requester.UserIdAsync();

    // The dataset is flagged before the job: a build that fails at once must find it flagged
    private async Task<string> EnqueueAsync(BuildTarget target, BuildOrigin? origin, string? path, string? hash,
        Func<IndexPayload, string> enqueue)
    {
        var userId = await UserIdAsync(origin);
        await FlagPendingAsync(target);
        return enqueue(Payload(target, userId, origin, path, hash));
    }

    private Task FlagPendingAsync(BuildTarget target) =>
        _pending.SetDatasetHasPendingBuilds(target.OrgSlug, target.DsSlug, true);

    private static IndexPayload Payload(BuildTarget target, string? userId, BuildOrigin? origin, string? path,
        string? hash) =>
        new(target.OrgSlug, target.DsSlug, hash, userId, null, path, ParentJobId: origin?.ParentJobId);
}
