using System;
using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The index fields of an entry without its properties, geometries and meta: what
/// <see cref="IDdbIndex.FindSummaries"/> returns for callers that only need paths, hashes,
/// types or sizes.
/// </summary>
public class EntrySummary
{
    [JsonProperty("path")]
    public string Path { get; set; }

    [JsonProperty("hash")]
    public string Hash { get; set; }

    [JsonProperty("type")]
    public EntryType Type { get; set; }

    [JsonProperty("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }

    [JsonProperty("size")]
    public long Size { get; set; }

    [JsonProperty("depth")]
    public int Depth { get; set; }
}
