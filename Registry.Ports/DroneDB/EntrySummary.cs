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
    public string Path { get; set; } = "";

    /// <summary>Absent for a directory (libddb writes no hash when it is empty).</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    [JsonPropertyName("type")]
    public EntryType Type { get; set; }

    [JsonPropertyName("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    /// <summary>
    /// The <see cref="Hash"/> of an entry the caller knows is a file: libddb hashes every entry but a directory.
    /// A method, not a property, so that no serializer writes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entry has no hash (a directory).</exception>
    public string FileHash() =>
        Hash ?? throw new InvalidOperationException($"The entry '{Path}' has no hash: it is a directory");
}
