#nullable enable
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

/// <summary>Default <see cref="IBuildScheduler"/>: indexed build jobs (<see cref="QueueDdbJobs"/>).</summary>
public sealed class BuildScheduler : IBuildScheduler
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

    public async Task<IReadOnlyList<QueuedBuild>> QueueBuildableAsync(BuildTarget target, IReadOnlyList<string> paths,
        BuildOrigin? origin = null, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return [];

        // One build object per path, in input order (the build manifest, XI-04)
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

    public async Task<string> QueueAsync(BuildTarget target, string path, string? hash, bool force = false,
        BuildOrigin? origin = null, CancellationToken ct = default)
    {
        var userId = await UserIdAsync(origin);
        await FlagPendingAsync(target);
        return _jobs.EnqueueBuild(target.Dataset, path, force, Payload(target, userId, origin, path, hash));
    }

    public async Task<string> QueueDatasetAsync(BuildTarget target, bool force, BuildOrigin? origin = null,
        CancellationToken ct = default)
    {
        var userId = await UserIdAsync(origin);
        await FlagPendingAsync(target);
        return _jobs.EnqueueBuild(target.Dataset, null, force, Payload(target, userId, origin, null, null));
    }

    public async Task<string?> QueuePendingAsync(BuildTarget target, BuildOrigin? origin = null,
        CancellationToken ct = default)
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

    // Before the job: a build that fails at once must find the dataset flagged
    private Task FlagPendingAsync(BuildTarget target) =>
        _pending.SetDatasetHasPendingBuilds(target.OrgSlug, target.DsSlug, true);

    private static IndexPayload Payload(BuildTarget target, string? userId, BuildOrigin? origin, string? path,
        string? hash) =>
        new(target.OrgSlug, target.DsSlug, hash, userId, null, path, ParentJobId: origin?.ParentJobId);
}
