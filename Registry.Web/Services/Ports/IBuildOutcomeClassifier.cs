using System.Collections.Generic;
using Registry.Ports.DroneDB;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Services.Ports;

/// <summary>Whether the end of a build job is the final outcome of the entry's build, the news an e-mail reports.</summary>
public interface IBuildOutcomeClassifier
{
    /// <summary>
    /// The outcome of the build of <paramref name="path"/> whose job just ended, as libddb sees the entry now: a
    /// succeeded job completed it only when libddb says it is built (not when another process held the build lock); a
    /// succeeded job that left the entry's pending marker waiting for files (libddb's <c>awaitingFiles</c>) waits for
    /// files, which it names; a failed job failed for good only when libddb keeps no pending marker for the entry (with
    /// one, the pending-build retries take over). Null when the end is no news.
    /// </summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="path">The entry.</param>
    /// <param name="jobHash">The hash the job built; null to take the entry's.</param>
    /// <param name="succeeded">Whether the job succeeded.</param>
    ClassifiedBuild? Classify(IDDB ddb, string path, string? jobHash, bool succeeded);

    /// <summary>
    /// <see cref="Classify"/> for several build jobs of one dataset that ended, one result per job in input order: one
    /// native call for their entries and at most one for the pending markers, whatever the number of jobs (the task
    /// lists).
    /// </summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="jobs">The jobs.</param>
    IReadOnlyList<ClassifiedBuild?> ClassifyAll(IDDB ddb, IReadOnlyList<EndedBuildJob> jobs);
}

/// <summary>A build job of an entry that ended.</summary>
/// <param name="Path">The entry.</param>
/// <param name="Hash">The hash the job built; null to take the entry's.</param>
/// <param name="Succeeded">Whether the job succeeded.</param>
public sealed record EndedBuildJob(string Path, string? Hash, bool Succeeded);
