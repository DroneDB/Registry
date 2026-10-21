using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// One artifact of a build, as libddb describes it (ddb.h "build manifest", XI-04). Registry serves
/// <see cref="Path"/> under <c>.../build/{hash}/</c> and never composes it.
/// </summary>
public sealed class BuildArtifact
{
    /// <summary>What the artifact is for (<see cref="ArtifactRoles"/>; unknown roles pass through).</summary>
    [JsonProperty("role")]
    public string Role { get; set; } = "";

    /// <summary>Relative to the build root, '/'-separated.</summary>
    [JsonProperty("path")]
    public string Path { get; set; } = "";

    /// <summary>"file", or "dir" for a directory (<see cref="Index"/> names its descriptor).</summary>
    [JsonProperty("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Media type of the file a client opens: the file, or the index of a directory.</summary>
    [JsonProperty("mediaType")]
    public string MediaType { get; set; } = "";

    /// <summary>The file name to offer for a download; null when the artifact is not one.</summary>
    [JsonProperty("downloadName", NullValueHandling = NullValueHandling.Ignore)]
    public string? DownloadName { get; set; }

    /// <summary>The descriptor file of a directory, relative to <see cref="Path"/>.</summary>
    [JsonProperty("index", NullValueHandling = NullValueHandling.Ignore)]
    public string? Index { get; set; }

    /// <summary>An output of an older layout: read when present, never written.</summary>
    [JsonProperty("legacy", DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool Legacy { get; set; }
}
