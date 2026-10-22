#nullable enable
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

/// <summary>
/// An artifact of an entry's build (XI-04): what it is for (libddb's role, passed through as libddb names it), where
/// to read it and the media type it is served with. A directory artifact is read through its descriptor (the
/// manifest's <c>index</c>, e.g. the descriptor of a tile pyramid): its URL is the descriptor's.
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
