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
/// Entries that arrive in a dataset (<see cref="QueueAddedAsync"/>) also start the retry of the pending builds
/// that waited for them (<see cref="RetryPendingAsync"/>).
/// </summary>
public sealed class BuildScheduler
{
    private readonly IBuildArtifactResolver _artifacts;
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly IPendingBuildFlag _pending;
    private readonly IPendingBuildRetry _retries;
    private readonly IBuildRequester _requester;
    private readonly ILogger<BuildScheduler> _logger;

    public BuildScheduler(IBuildArtifactResolver artifacts, IBackgroundJobsProcessor jobs, IPendingBuildFlag pending,
        IPendingBuildRetry retries, IBuildRequester requester, ILogger<BuildScheduler> logger)
    {
        _artifacts = artifacts;
        _jobs = jobs;
        _pending = pending;
        _retries = retries;
        _requester = requester;
        _logger = logger;
    }

    /// <summary>
    /// What the arrival of <paramref name="paths"/> in the dataset calls for: a build of each buildable one
    /// (<see cref="QueueBuildableAsync"/>), then the retry of the pending builds that waited for one of them
    /// (<see cref="RetryPendingAsync"/>), as the one call of every path that adds entries.
    /// </summary>
    /// <returns>The builds queued (the buildable paths).</returns>
    public async Task<IReadOnlyList<QueuedBuild>> QueueAddedAsync(BuildTarget target, IReadOnlyList<string> paths,
        BuildOrigin? origin = null, CancellationToken ct = default)
    {
        var queued = await QueueBuildableAsync(target, paths, origin, ct);
        await RetryPendingAsync(target);
        return queued;
    }

    /// <summary>
    /// Starts the pending-build coordinator when libddb reports pending work a retry can progress and none is live:
    /// libddb makes a build that waited for missing files due when one of them is added or moved into the dataset,
    /// and a non-buildable file (an .mtl, a .dbf) queues no build whose end would start the retry. One native call
    /// when nothing is retryable; never throws.
    /// </summary>
    /// <returns>The coordinator's job id, or null when none was started.</returns>
    public Task<string?> RetryPendingAsync(BuildTarget target) => _retries.StartIfRetryableAsync(target);

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
            var jobId = Enqueue(target.Dataset, build.Path, false, background: false,
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
        EnqueueAsync(target, origin, path, hash,
            payload => Enqueue(target.Dataset, path, force, background: false, payload));

    /// <summary>
    /// Queues the build of <paramref name="path"/> on the background queue, for an entry nobody waits for (the check
    /// for entries of an older layout): the same bookkeeping as <see cref="QueueAsync"/>, another queue and a
    /// capped number of threads.
    /// </summary>
    /// <returns>The job id.</returns>
    public Task<string> QueueRebuildAsync(BuildTarget target, string path, string? hash, BuildOrigin? origin = null) =>
        EnqueueAsync(target, origin, path, hash,
            payload => Enqueue(target.Dataset, path, false, background: true, payload));

    /// <summary>Queues a build of the whole dataset (an import, a rescan).</summary>
    /// <returns>The job id.</returns>
    public Task<string> QueueDatasetAsync(BuildTarget target, bool force, BuildOrigin? origin = null) =>
        EnqueueAsync(target, origin, null, null,
            payload => Enqueue(target.Dataset, null, force, background: false, payload));

    /// <summary>
    /// Queues the build of an entry whose build libddb deferred (the pending-build coordinator, which runs inside a
    /// job under the dataset's build lock): on the background queue when <paramref name="background"/> is set, as
    /// <see cref="QueueRebuildAsync"/> does. The dataset is already flagged as having pending builds, which is
    /// how the entry was found, and <paramref name="payload"/> is the one the coordinator built for the entry.
    /// </summary>
    /// <returns>The job id.</returns>
    public string QueueDeferred(DatasetKey dataset, string path, bool background, IndexPayload payload) =>
        Enqueue(dataset, path, false, background, payload);

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

    // The one place that picks the job of a build: a build, or the background rebuild of an entry nobody waits for
    private string Enqueue(DatasetKey dataset, string? path, bool force, bool background, IndexPayload payload)
    {
        if (!background)
            return _jobs.EnqueueBuild(dataset, path, force, payload);

        ArgumentNullException.ThrowIfNull(path); // a background rebuild is always of an entry
        return _jobs.EnqueueRebuild(dataset, path, payload);
    }

    private Task FlagPendingAsync(BuildTarget target) =>
        _pending.SetDatasetHasPendingBuilds(target.OrgSlug, target.DsSlug, true);

    private static IndexPayload Payload(BuildTarget target, string? userId, BuildOrigin? origin, string? path,
        string? hash) =>
        new(target.OrgSlug, target.DsSlug, hash, userId, null, path, ParentJobId: origin?.ParentJobId);
}
