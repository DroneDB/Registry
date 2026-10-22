#nullable enable
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

/// <summary>
/// An artifact of an entry's build (XI-04): what it is for (libddb's role, passed through as libddb names it), where
/// to read it and the media type it is served with. A directory artifact is read through its descriptor (the MVT
/// <c>metadata.json</c>, the 3D Tiles <c>tileset.json</c>): its URL is the descriptor's.
/// </summary>
public sealed class ArtifactLinkDto
{
    [JsonProperty("role")]
    public string Role { get; init; } = "";

    [JsonProperty("url")]
    public string Url { get; init; } = "";

    [JsonProperty("mediaType")]
    public string MediaType { get; init; } = "";
}
