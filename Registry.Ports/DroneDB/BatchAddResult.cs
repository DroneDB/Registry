using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// An entry produced by a batch add, tagged with whether it was newly created or updated.
/// Separate from <see cref="Entry"/> (used pervasively elsewhere) to avoid widening that
/// shared wire contract for a field only the batch-add completeness response needs.
/// </summary>
public class BatchAddedEntry : Entry
{
    /// <summary>"added" or "updated".</summary>
    [JsonProperty("status")]
    public string Status { get; set; }
}

/// <summary>A path that failed to be added, isolated from the rest of the batch.</summary>
public class BatchAddItemError
{
    [JsonProperty("path")]
    public string Path { get; set; }

    /// <summary>"FS" | "GDAL" | "PDAL" | "JSON" | "INDEX" | "CONFLICT".</summary>
    [JsonProperty("code")]
    public string Code { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; }
}

/// <summary>
/// Result of a batch add with per-item error isolation (DDBAddWithOptions). Every input path
/// appears in exactly one of <see cref="Entries"/>, <see cref="Unchanged"/> or <see cref="Errors"/>
/// (completeness contract).
/// </summary>
public class BatchAddResult
{
    [JsonProperty("entries")]
    public List<BatchAddedEntry> Entries { get; set; } = new();

    [JsonProperty("unchanged")]
    public List<BatchAddUnchangedItem> Unchanged { get; set; } = new();

    [JsonProperty("errors")]
    public List<BatchAddItemError> Errors { get; set; } = new();
}

/// <summary>A path that was already up to date and required no write.</summary>
public class BatchAddUnchangedItem
{
    [JsonProperty("path")]
    public string Path { get; set; }
}
