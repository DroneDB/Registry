using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>One element of <c>DDBGetBuildArtifacts</c>: an entry path and its build object.</summary>
public sealed class EntryBuildArtifacts
{
    /// <summary>Dataset-relative entry path, as requested.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>The index has an entry with this path (when false, the other members are empty).</summary>
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>The entry's hash; null for a folder or a path that is not indexed.</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("type")]
    public EntryType Type { get; set; }

    /// <summary>The build object; null for a path that is not indexed.</summary>
    [JsonPropertyName("build")]
    public EntryBuild? Build { get; set; }
}
