using System;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The index fields of an entry without its properties, geometries and meta: what
/// <see cref="IDdbIndex.FindSummaries"/> returns for callers that only need paths, hashes,
/// types or sizes.
/// </summary>
public class EntrySummary
{
    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; }

    [JsonPropertyName("type")]
    public EntryType Type { get; set; }

    [JsonPropertyName("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }
}
