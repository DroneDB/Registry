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
