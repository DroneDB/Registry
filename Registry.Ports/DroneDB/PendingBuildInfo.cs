using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Information about a build deferred because one or more dependencies
/// (companion/sidecar files or external tools) were missing at the time of
/// the last attempt. Returned by the native DDBGetPendingBuildInfo C API.
/// </summary>
public class PendingBuildInfo
{
    /// <summary>Hash of the entry's content.</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>
    /// The build key of the entry (<see cref="EntryBuild.Key"/>): the marker is the entry's own (libddb names it by
    /// this key and the entry's path), so two entries with the same content have two markers. Absent from a libddb
    /// that predates the build key: <see cref="BuildKey"/> then reads the hash.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>The key of the build the marker is for: <see cref="Key"/>, else <see cref="Hash"/> (an older libddb).</summary>
    [JsonIgnore]
    public string BuildKey => Key ?? Hash;

    /// <summary>Dataset-relative path of the entry whose build is pending.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Dependency names still missing at the time of the last attempt.</summary>
    [JsonPropertyName("missingDependencies")]
    public string[] MissingDependencies { get; set; } = [];

    /// <summary>Unix timestamp of the last build attempt.</summary>
    [JsonPropertyName("lastAttempt")]
    public long LastAttempt { get; set; }

    /// <summary>
    /// Unix timestamp from which libddb retries the build (<c>lastAttempt</c> + its retry backoff):
    /// the retry is due when the current time is at or past it. libddb owns the rule, so Registry
    /// keeps no copy of the backoff. Required: a libddb without the field fails deserialization
    /// instead of making every entry due at once (a missing long would read as 0).
    /// </summary>
    [JsonPropertyName("retryAfter")]
    [JsonRequired]
    public long RetryAfter { get; set; }

    /// <summary>
    /// The build waits for missing files (a companion such as the .mtl of an .obj): no timed retry can
    /// progress it, so it runs again when one of those files is added to the dataset (libddb then makes
    /// it due) and never on a timer. Absent from an older libddb, which reads as false.
    /// </summary>
    [JsonPropertyName("awaitingFiles")]
    public bool AwaitingFiles { get; set; }
}
