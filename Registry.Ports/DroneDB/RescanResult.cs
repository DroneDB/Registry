using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Represents the result of a rescan operation for a single entry
/// </summary>
public class RescanResult
{
    /// <summary>
    /// The path of the scanned entry
    /// </summary>
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The hash of the entry after rescan
    /// </summary>
    [JsonProperty("hash")]
    public string? Hash { get; set; }

    /// <summary>
    /// Whether the rescan was successful for this entry
    /// </summary>
    [JsonProperty("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the rescan failed for this entry
    /// </summary>
    [JsonProperty("error")]
    public string? Error { get; set; }
}
