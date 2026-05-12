using System;
using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Result of a DroneDB cleanup operation on a single dataset.
/// Returned by the native DDBCleanup C API.
/// </summary>
public class DdbCleanupResult
{
    /// <summary>
    /// Paths of entries removed from the database because their underlying
    /// file no longer existed on the filesystem.
    /// </summary>
    [JsonProperty("entries")]
    public string[] Entries { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Hashes of orphaned build artifacts that were removed.
    /// </summary>
    [JsonProperty("builds")]
    public string[] Builds { get; set; } = Array.Empty<string>();
}
