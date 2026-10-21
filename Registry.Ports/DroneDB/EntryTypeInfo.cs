using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// An entry type and what libddb does with it (<c>DDBGetEntryTypes</c>, XI-04): the one source of
/// "which types build, which have thumbnails, which extensions are sidecars".
/// </summary>
public sealed class EntryTypeInfo
{
    [JsonProperty("id")]
    public EntryType Id { get; set; }

    /// <summary>Its name, as DDBRescan takes it (case-insensitive).</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("buildable")]
    public bool Buildable { get; set; }

    /// <summary>"file": rendered from the entry's file; "build": from an artifact once built; "none".</summary>
    [JsonProperty("thumbnail")]
    public string Thumbnail { get; set; } = "";

    /// <summary>Lowercase extensions, without the dot, of the files that belong to a main file of this type.</summary>
    [JsonProperty("sidecarExtensions")]
    public IReadOnlyList<string> SidecarExtensions { get; set; } = [];

    /// <summary>Every artifact role a build of this type can produce, legacy ones included.</summary>
    [JsonProperty("roles")]
    public IReadOnlyList<string> Roles { get; set; } = [];
}
