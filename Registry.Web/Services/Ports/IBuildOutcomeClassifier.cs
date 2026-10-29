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
}
