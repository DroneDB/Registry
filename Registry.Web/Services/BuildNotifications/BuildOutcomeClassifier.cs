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
/// (<see cref="IBuildArtifactResolver.GetBuild"/>, <see cref="IBuildArtifactResolver.GetBuilds"/> for several) and its pending markers (<see cref="IDdbBuild.GetPendingBuildInfo"/>),
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

        return Decide(_builds.GetBuild(ddb, path), path, jobHash, succeeded, () => ddb.GetPendingBuildInfo());
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassifiedBuild?> ClassifyAll(IDDB ddb, IReadOnlyList<EndedBuildJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
            return [];

        string[] paths = [.. jobs.Select(j => j.Path).Distinct()];
        // One element per path, in input order
        var entryByPath = paths.Zip(_builds.GetBuilds(ddb, paths)).ToDictionary(p => p.First, p => p.Second);

        // Read once, and only when a job needs the markers
        PendingBuildInfo[]? markers = null;
        return [.. jobs.Select(job => entryByPath.TryGetValue(job.Path, out var entry)
            ? Decide(entry, job.Path, job.Hash, job.Succeeded, () => markers ??= ddb.GetPendingBuildInfo())
            : null)];
    }

    // The one rule of both: the entry as libddb's manifest has it now, and its pending markers, read only when needed
    private static ClassifiedBuild? Decide(EntryBuildArtifacts entry, string path, string? jobHash, bool succeeded,
        Func<PendingBuildInfo[]?> markers)
    {
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
            if (build.State == BuildState.Building ||
                PendingOf(markers(), path, hash) is not { AwaitingFiles: true } waiting)
                return null;
            return new ClassifiedBuild(BuildOutcome.WaitingForFiles, hash, waiting.MissingDependencies ?? []);
        }

        // Built meanwhile, or being built now, by another job: its end is the news
        if (build.State is BuildState.Complete or BuildState.Building)
            return null;

        // libddb keeps the marker of a failure it retries (low disk, a tool timeout): the retries decide
        return PendingOf(markers(), path, hash) == null ? new ClassifiedBuild(BuildOutcome.Failed, hash) : null;
    }

    // libddb's pending marker of this content of the entry; null when it has none
    private static PendingBuildInfo? PendingOf(PendingBuildInfo[]? markers, string path, string hash) =>
        (markers ?? []).FirstOrDefault(p => p.Path == path && p.Hash == hash);
}
