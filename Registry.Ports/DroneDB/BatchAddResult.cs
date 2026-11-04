using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// An entry produced by a batch add, tagged with whether it was newly created or updated.
/// Separate from <see cref="Entry"/> (used pervasively elsewhere) to avoid widening that
/// shared wire contract for a field only the batch-add completeness response needs.
/// </summary>
public class BatchAddedEntry : Entry
{
    /// <summary>"added" or "updated".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

/// <summary>A path that failed to be added, isolated from the rest of the batch.</summary>
public class BatchAddItemError
{
    /// <summary>The values of <see cref="Code"/> (libddb's <c>AddItemError</c>).</summary>
    public static class Codes
    {
        /// <summary>The file could not be read from the file system.</summary>
        public const string FileSystem = "FS";

        /// <summary>GDAL could not read the file (a raster or a vector).</summary>
        public const string Gdal = "GDAL";

        /// <summary>PDAL could not read the file (a point cloud).</summary>
        public const string Pdal = "PDAL";

        /// <summary>The file is not valid JSON.</summary>
        public const string Json = "JSON";

        /// <summary>The path cannot be indexed (a separator in a name, a reserved name).</summary>
        public const string Index = "INDEX";

        /// <summary>The entry kept changing under the add.</summary>
        public const string Conflict = "CONFLICT";
    }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>One of <see cref="Codes"/>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>
    /// The exception of this failure: a file whose content its reader refused is
    /// <see cref="DdbInvalidContentException"/> and a path that cannot be indexed
    /// <see cref="DdbInvalidArgumentsException"/> (both the client's data, never retried); anything else
    /// (the file system, a conflict, a code added later) stays a <see cref="DdbException"/>.
    /// </summary>
    public DdbException ToException() => Code switch
    {
        Codes.Gdal or Codes.Pdal or Codes.Json =>
            new DdbInvalidContentException($"'{Path}' cannot be read as its file type: {Message}"),
        Codes.Index => new DdbInvalidArgumentsException($"'{Path}' cannot be indexed: {Message}"),
        _ => new DdbException($"[{Code}] {Message}")
    };
}

/// <summary>
/// Result of a batch add with per-item error isolation (DDBAddWithOptions). Every input path
/// appears in exactly one of <see cref="Entries"/>, <see cref="Unchanged"/> or <see cref="Errors"/>
/// (completeness contract).
/// </summary>
public class BatchAddResult
{
    [JsonPropertyName("entries")]
    public List<BatchAddedEntry> Entries { get; set; } = new();

    [JsonPropertyName("unchanged")]
    public List<BatchAddUnchangedItem> Unchanged { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<BatchAddItemError> Errors { get; set; } = new();
}

/// <summary>A path that was already up to date and required no write.</summary>
public class BatchAddUnchangedItem
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}
