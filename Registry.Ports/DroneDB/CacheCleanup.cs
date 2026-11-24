using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// The ages, in days since the last change, past which <c>DDBCleanupCache</c> removes what libddb's cache and the
/// temporary folder kept; a member left null keeps libddb's default (tiles 5, downloads 7, DSM tiles 30, temporary
/// folders 2).
/// </summary>
/// <param name="TileDays">The folders of rendered tiles.</param>
/// <param name="DownloadDays">The copies of remote rasters that tile requests downloaded.</param>
/// <param name="DsmDays">The elevation tiles of the DSM service.</param>
/// <param name="TempDays">The request folders no request holds and libddb's folders in the temporary folder.</param>
/// <param name="DryRun">Count what would go and remove nothing.</param>
public sealed record CacheCleanupOptions(
    int? TileDays = null,
    int? DownloadDays = null,
    int? DsmDays = null,
    int? TempDays = null,
    bool DryRun = false)
{
    /// <summary>The longest age libddb accepts (100 years); 0 is the shortest, which removes every entry.</summary>
    public const int MaxDays = 36500;
}

/// <summary>What <c>DDBCleanupCache</c> removed of one kind of entry, or would remove in a dry run.</summary>
public sealed class CacheCleanupCount
{
    /// <summary>Files or folders removed.</summary>
    [JsonPropertyName("entries")]
    public long Entries { get; set; }

    /// <summary>The bytes of their files.</summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }
}

/// <summary>
/// The output of <c>DDBCleanupCache</c> (ABI 2.13): <c>{"dryRun", "kinds": {kind: {"entries", "bytes"}}}</c>, every kind
/// libddb cleans (<c>tiles</c>, <c>downloads</c>, <c>dsm</c>, <c>requests</c>, <c>temp</c>, <c>artefacts</c>). The kinds
/// are a map rather than members: libddb only ever adds kinds, and a new one is then reported without a change here.
/// </summary>
public sealed class CacheCleanupResult
{
    /// <summary>True when nothing was removed, only counted.</summary>
    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }

    /// <summary>What was removed, by kind.</summary>
    [JsonPropertyName("kinds")]
    public Dictionary<string, CacheCleanupCount> Kinds { get; set; } = [];

    /// <summary>The entries of every kind together.</summary>
    [JsonIgnore]
    public long TotalEntries => Kinds.Values.Sum(k => k.Entries);

    /// <summary>The bytes of every kind together.</summary>
    [JsonIgnore]
    public long TotalBytes => Kinds.Values.Sum(k => k.Bytes);
}
