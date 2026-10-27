#nullable enable
using System;
using System.Linq;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>The final outcome of an entry's build and the hash it is for.</summary>
public sealed record ClassifiedBuild(BuildOutcome Outcome, string Hash);

/// <summary>
/// <see cref="IBuildOutcomeClassifier"/> from libddb's answers only: the build state of the entry
/// (<see cref="IBuildArtifactResolver.GetBuild"/>) and its pending markers (<see cref="IDdbBuild.GetPendingBuildInfo"/>),
/// the same marker the pending-build retry looks for. No build path is composed here: libddb owns the build layout.
/// </summary>
public sealed class BuildOutcomeClassifier : IBuildOutcomeClassifier
{
    private readonly IBuildArtifactResolver _builds;

    public BuildOutcomeClassifier(IBuildArtifactResolver builds)
    {
        _builds = builds ?? throw new ArgumentNullException(nameof(builds));
    }

    /// <inheritdoc />
    public ClassifiedBuild? Classify(IDDB ddb, string path, string? jobHash, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var entry = _builds.GetBuild(ddb, path);
        // Gone since, or nothing to build: no news
        if (!entry.Exists || entry.Build is not { Buildable: true } build)
            return null;

        var hash = string.IsNullOrEmpty(jobHash) ? entry.Hash : jobHash;
        if (string.IsNullOrEmpty(hash))
            return null;

        // A success that left the entry pending (a missing companion file) or skipped it (another process holds the
        // lock and builds it: that build reports) is not the end
        if (succeeded)
            return build.State == BuildState.Complete ? new ClassifiedBuild(BuildOutcome.Completed, hash) : null;

        // Built meanwhile, or being built now, by another job: its end is the news
        if (build.State is BuildState.Complete or BuildState.Building)
            return null;

        // libddb keeps the marker of a failure it retries (low disk, a tool timeout): the retries decide
        var retried = (ddb.GetPendingBuildInfo() ?? []).Any(p => p.Path == path && p.Hash == hash);
        return retried ? null : new ClassifiedBuild(BuildOutcome.Failed, hash);
    }
}
