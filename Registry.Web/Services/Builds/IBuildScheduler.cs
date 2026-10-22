#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Builds;

/// <summary>
/// The one place that queues builds of a dataset (RQ-12): whether an entry is buildable and whether a dataset has
/// deferred builds comes from libddb (the build manifest, XI-04); the scheduler only queues. Before the first
/// job of a call is queued, the dataset is flagged as having pending builds, so a build that fails at once is
/// retried by the pending-build coordinator (the flag must precede the job, or a quick failure is missed).
/// </summary>
public interface IBuildScheduler
{
    /// <summary>
    /// Queues a build of each of <paramref name="paths"/> libddb reports buildable (one manifest call), in order.
    /// </summary>
    /// <returns>The builds queued (the buildable paths).</returns>
    Task<IReadOnlyList<QueuedBuild>> QueueBuildableAsync(BuildTarget target, IReadOnlyList<string> paths,
        BuildOrigin? origin = null, CancellationToken ct = default);

    /// <summary>
    /// Queues the build of <paramref name="path"/> the caller decided on (its buildability and live-build checks
    /// are the caller's, as for the build endpoint).
    /// </summary>
    /// <returns>The job id.</returns>
    Task<string> QueueAsync(BuildTarget target, string path, string? hash, bool force = false,
        BuildOrigin? origin = null, CancellationToken ct = default);

    /// <summary>Queues a build of the whole dataset (an import, a rescan).</summary>
    /// <returns>The job id.</returns>
    Task<string> QueueDatasetAsync(BuildTarget target, bool force, BuildOrigin? origin = null,
        CancellationToken ct = default);

    /// <summary>Queues the pending-build job when libddb reports deferred builds in the dataset.</summary>
    /// <returns>The job id, or null when nothing is deferred.</returns>
    Task<string?> QueuePendingAsync(BuildTarget target, BuildOrigin? origin = null, CancellationToken ct = default);
}

/// <summary>The dataset whose entries are built: its index, how a job names it, its slugs (job index, cache).</summary>
public sealed record BuildTarget(IDDB Ddb, DatasetKey Dataset, string OrgSlug, string DsSlug)
{
    /// <summary>The dataset a request resolved.</summary>
    public static BuildTarget Of(DatasetAccess access, string orgSlug, string dsSlug) =>
        new(access.Ddb, access.Key, orgSlug, dsSlug);
}

/// <summary>
/// Who the builds are for: the user they run as (null: the service) and the task they belong to. Null for a
/// scheduler call means the requester of the host (<see cref="IBuildRequester"/>: the signed-in user of a
/// request; nobody in a job host).
/// </summary>
public sealed record BuildOrigin(string? UserId, string? ParentJobId = null)
{
    /// <summary>The automatic builds Registry starts itself (the completeness sweep).</summary>
    public static BuildOrigin Service { get; } = new(MagicStrings.AutoBuildServiceUserId);
}

/// <summary>A build job queued for an entry.</summary>
public sealed record QueuedBuild(string Path, string? Hash, string JobId);

/// <summary>
/// The observation the pending-build coordinator reads (<see cref="BuildPendingService"/>): the dataset may have
/// builds that wait or fail, so its next sweep looks at it.
/// </summary>
public interface IPendingBuildFlag
{
    /// <summary>Records whether <paramref name="orgSlug"/>/<paramref name="dsSlug"/> may have pending builds.</summary>
    Task SetDatasetHasPendingBuilds(string orgSlug, string dsSlug, bool hasPending);
}

/// <summary>The default requester of builds queued without a <see cref="BuildOrigin"/>.</summary>
public interface IBuildRequester
{
    /// <summary>The id of the user the builds run as, or null for the service.</summary>
    Task<string?> UserIdAsync();
}
