using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Buildability and build completeness of one entry, as returned by the native
/// DDBGetBuildStatus C API (one element per requested path, in request order).
/// </summary>
public class EntryBuildStatus
{
    /// <summary>Dataset-relative entry path, as requested.</summary>
    [JsonProperty("path")]
    public string Path { get; set; }

    /// <summary>The index has an entry with this path.</summary>
    [JsonProperty("exists")]
    public bool Exists { get; set; }

    /// <summary>Same answer as <see cref="IDdbBuild.IsBuildable"/> (false when the entry does not exist).</summary>
    [JsonProperty("buildable")]
    public bool Buildable { get; set; }

    /// <summary>Same answer as <see cref="IDdbBuild.IsBuildComplete"/>.</summary>
    [JsonProperty("complete")]
    public bool Complete { get; set; }
}
