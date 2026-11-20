using Registry.Ports.DroneDB;

namespace Registry.Adapters.Testing;

/// <summary>
/// Pending build markers as libddb reports them (<c>DDBGetPendingBuildInfo</c>,
/// <c>fixtures/pending-build-info.json</c>), for tests that need one with given values.
/// </summary>
public static class PendingMarkers
{
    /// <summary>
    /// libddb's pending-build retry backoff (ddb.h "Build manifest": <c>retryAfter</c> =
    /// <c>lastAttempt</c> + 60). Test data only: Registry reads <c>retryAfter</c> and keeps no copy
    /// of the rule; the native contract test checks that libddb still applies this value.
    /// </summary>
    public const long LibddbRetryBackoffSeconds = 60;

    /// <summary>
    /// The marker of <paramref name="path"/> last attempted at <paramref name="lastAttempt"/> (Unix
    /// seconds), with the <see cref="PendingBuildInfo.RetryAfter"/> libddb derives from it.
    /// </summary>
    public static PendingBuildInfo Of(string path, string hash, long lastAttempt = 0,
        params string[] missingDependencies) => new()
    {
        Path = path,
        Hash = hash,
        MissingDependencies = missingDependencies,
        LastAttempt = lastAttempt,
        RetryAfter = lastAttempt + LibddbRetryBackoffSeconds
    };
}
