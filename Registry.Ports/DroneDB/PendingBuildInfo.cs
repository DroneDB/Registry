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
}
