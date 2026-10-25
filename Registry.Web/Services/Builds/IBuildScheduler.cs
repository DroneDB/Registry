#nullable enable
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Builds;

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
