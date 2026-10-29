using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>The outcome of an entry's build to tell, and the hash it is for.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Hash">The hash of the entry it built.</param>
/// <param name="MissingFiles">
/// The files it waits for, as libddb names them (dataset-relative), when it is <see cref="BuildOutcome.WaitingForFiles"/>;
/// empty otherwise.
/// </param>
public sealed record ClassifiedBuild(BuildOutcome Outcome, string Hash, IReadOnlyList<string> MissingFiles)
{
    public ClassifiedBuild(BuildOutcome outcome, string hash) : this(outcome, hash, [])
    {
    }
}

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

        if (succeeded)
        {
            if (build.State == BuildState.Complete)
                return new ClassifiedBuild(BuildOutcome.Completed, hash);

            // A build that finds files missing ends without building (the adapter takes libddb's deferral for a
            // success) and leaves the marker of this content waiting for them: the one sign that the build found a file
            // missing. A success that skipped the entry (another process holds the lock and builds it: that build
            // reports), or whose marker a file that arrived meanwhile already released (it builds now), tells nothing
            if (build.State == BuildState.Building || PendingOf(ddb, path, hash) is not { AwaitingFiles: true } waiting)
                return null;
            return new ClassifiedBuild(BuildOutcome.WaitingForFiles, hash, waiting.MissingDependencies ?? []);
        }

        // Built meanwhile, or being built now, by another job: its end is the news
        if (build.State is BuildState.Complete or BuildState.Building)
            return null;

        // libddb keeps the marker of a failure it retries (low disk, a tool timeout): the retries decide
        return PendingOf(ddb, path, hash) == null ? new ClassifiedBuild(BuildOutcome.Failed, hash) : null;
    }

    // libddb's pending marker of this content of the entry; null when it has none
    private static PendingBuildInfo? PendingOf(IDDB ddb, string path, string hash) =>
        (ddb.GetPendingBuildInfo() ?? []).FirstOrDefault(p => p.Path == path && p.Hash == hash);
}
