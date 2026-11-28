using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// An entry type and what libddb does with it (<c>DDBGetEntryTypes</c>): the one source of
/// "which types build, which have thumbnails, which extensions are sidecars".
/// </summary>
public sealed class EntryTypeInfo
{
    [JsonPropertyName("id")]
    public EntryType Id { get; set; }

    /// <summary>Its name, as DDBRescan takes it (case-insensitive).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("buildable")]
    public bool Buildable { get; set; }

    /// <summary>"file": rendered from the entry's file; "build": from an artifact once built; "none".</summary>
    [JsonPropertyName("thumbnail")]
    public string Thumbnail { get; set; } = "";

    /// <summary>The type has a thumbnail, rendered from the entry's file or from a build artifact.</summary>
    [JsonIgnore]
    public bool HasThumbnail => Thumbnail is "file" or "build";

    /// <summary>Lowercase extensions, without the dot, of the files that belong to a main file of this type.</summary>
    [JsonPropertyName("sidecarExtensions")]
    public IReadOnlyList<string> SidecarExtensions { get; set; } = [];

    /// <summary>Every artifact role a build of this type can produce, legacy ones included.</summary>
    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; set; } = [];
}
