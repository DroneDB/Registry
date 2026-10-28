#nullable disable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

public class StacCollectionsDto
{
    [JsonPropertyName("collections")]
    public IEnumerable<object> Collections { get; set; }

    [JsonPropertyName("links")]
    public IEnumerable<StacLinkDto> Links { get; set; }

    [JsonPropertyName("numberMatched")]
    public int NumberMatched { get; set; }

    [JsonPropertyName("numberReturned")]
    public int NumberReturned { get; set; }
}
