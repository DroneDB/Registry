using System;
using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Information about a build deferred because one or more dependencies
/// (companion/sidecar files or external tools) were missing at the time of
/// the last attempt. Returned by the native DDBGetPendingBuildInfo C API.
/// </summary>
public class PendingBuildInfo
{
    /// <summary>Entry hash (matches the ".pending" marker file on disk).</summary>
    [JsonProperty("hash")]
    public string Hash { get; set; }

    /// <summary>Dataset-relative path of the entry whose build is pending.</summary>
    [JsonProperty("path")]
    public string Path { get; set; }

    /// <summary>Dependency names still missing at the time of the last attempt.</summary>
    [JsonProperty("missingDependencies")]
    public string[] MissingDependencies { get; set; } = [];

    /// <summary>Unix timestamp of the last build attempt.</summary>
    [JsonProperty("lastAttempt")]
    public long LastAttempt { get; set; }

    /// <summary>
    /// Unix timestamp from which libddb retries the build (<c>lastAttempt</c> + its retry backoff):
    /// the retry is due when the current time is at or past it. libddb owns the rule, so Registry
    /// keeps no copy of the backoff. Required: a libddb without the field fails deserialization
    /// instead of making every entry due at once (a missing long would read as 0).
    /// </summary>
    [JsonProperty("retryAfter", Required = Required.Always)]
    public long RetryAfter { get; set; }
}
