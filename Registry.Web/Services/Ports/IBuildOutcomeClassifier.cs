#nullable enable
using Registry.Ports.DroneDB;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Services.Ports;

/// <summary>Whether the end of a build job is the final outcome of the entry's build, the news an e-mail reports.</summary>
public interface IBuildOutcomeClassifier
{
    /// <summary>
    /// The final outcome of the build of <paramref name="path"/> whose job just ended, as libddb sees the entry now: a
    /// succeeded job completed it only when libddb says it is built (not when a dependency is missing or another process
    /// held the build lock); a failed job failed for good only when libddb keeps no pending marker for the entry (with
    /// one, the pending-build retries take over). Null when the end is not final.
    /// </summary>
    /// <param name="ddb">The dataset.</param>
    /// <param name="path">The entry.</param>
    /// <param name="jobHash">The hash the job built; null to take the entry's.</param>
    /// <param name="succeeded">Whether the job succeeded.</param>
    ClassifiedBuild? Classify(IDDB ddb, string path, string? jobHash, bool succeeded);
}
