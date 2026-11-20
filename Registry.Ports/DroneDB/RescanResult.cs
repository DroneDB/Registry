using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Represents the result of a rescan operation for a single entry
/// </summary>
public class RescanResult
{
    /// <summary>
    /// The path of the scanned entry
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The hash of the entry after rescan
    /// </summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>
    /// The build key of the entry after the rescan (<see cref="EntryBuild.Key"/>); absent from a libddb that
    /// predates the build key, and from a failed entry.
    /// </summary>
    [JsonPropertyName("buildKey")]
    public string? BuildKey { get; set; }

    /// <summary>
    /// The key is not the one of the build the entry had before the rescan, so its build must be made again (D-020):
    /// its content or its georeference changed, or its build is still in the folder named by the hash alone, from
    /// before the build key. Absent from an older libddb, which has no such builds to move: false.
    /// </summary>
    [JsonPropertyName("buildKeyChanged")]
    public bool BuildKeyChanged { get; set; }

    /// <summary>
    /// Whether the rescan was successful for this entry
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the rescan failed for this entry
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
