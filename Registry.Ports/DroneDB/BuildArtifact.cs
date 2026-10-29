using System;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

/// <summary>
/// One artifact of a build, as libddb describes it (ddb.h "build manifest"). Registry serves
/// <see cref="Path"/> under <c>.../build/{hash}/</c> and never composes it.
/// </summary>
public sealed class BuildArtifact
{
    /// <summary>What the artifact is for (<see cref="ArtifactRoles"/>; unknown roles pass through).</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    /// <summary>Relative to the build root, '/'-separated.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>The <see cref="Kind"/> of a directory artifact.</summary>
    public const string DirectoryKind = "dir";

    /// <summary>"file", or "dir" for a directory (<see cref="Index"/> names its descriptor).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>The artifact is a directory (a tile pyramid): a download of it is a ZIP archive of what is in it.</summary>
    [JsonIgnore]
    public bool IsDirectory => string.Equals(Kind, DirectoryKind, StringComparison.Ordinal);

    /// <summary>Media type of the file a client opens: the file, or the index of a directory.</summary>
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "";

    /// <summary>
    /// The file name to offer for a download; null when the artifact is not one. A directory is offered as a ZIP
    /// archive of what is in it, under this name.
    /// </summary>
    [JsonPropertyName("downloadName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadName { get; set; }

    /// <summary>The descriptor file of a directory, relative to <see cref="Path"/>.</summary>
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Index { get; set; }

    /// <summary>An output of an older layout: read when present, never written.</summary>
    [JsonPropertyName("legacy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Legacy { get; set; }

    /// <summary>
    /// Legacy artifacts only: the role this one stands in for. libddb lists the artifact only while that role is
    /// not listed (<see cref="ArtifactRoles.ModelNexus"/> for <see cref="ArtifactRoles.ModelBm3d"/>,
    /// <see cref="ArtifactRoles.PointCloudEpt"/> for <see cref="ArtifactRoles.PointCloudCopc"/>).
    /// </summary>
    [JsonPropertyName("fallbackFor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackFor { get; set; }

    /// <summary>
    /// Identifies the bytes of the file a client opens (the file, or the <see cref="Index"/> of a directory) as they
    /// are now: libddb changes it whenever a build rewrites or replaces that file under the same hash (a forced
    /// rebuild, a rebuild to a newer layout). Opaque, compared and never parsed; null from a libddb without it.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
}
