using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

/// <summary>
/// An artifact of an entry's build: what it is for (libddb's role, passed through as libddb names it), where
/// to read it and the media type it is served with. A directory artifact is read through its descriptor (the
/// manifest's <c>index</c>, e.g. the descriptor of a tile pyramid): its URL is the descriptor's. An artifact with a
/// download name can be downloaded (<c>build-artifact</c> with its role).
/// </summary>
public sealed class ArtifactLinkDto
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("mediaType")]
    public string MediaType { get; init; } = "";

    /// <summary>
    /// The manifest's download name when the artifact is offered as a download (<c>build-artifact</c> with its role),
    /// else null; a directory is offered as a ZIP archive.
    /// </summary>
    [JsonPropertyName("downloadName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadName { get; init; }
}
