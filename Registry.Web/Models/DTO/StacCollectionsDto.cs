using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class StacCollectionsDto
{
    [JsonProperty("collections")]
    public IEnumerable<object> Collections { get; set; }

    [JsonProperty("links")]
    public IEnumerable<StacLinkDto> Links { get; set; }

    [JsonProperty("numberMatched")]
    public int NumberMatched { get; set; }

    [JsonProperty("numberReturned")]
    public int NumberReturned { get; set; }
}
