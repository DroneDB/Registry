using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Web.Models.DTO;

public class StacCollectionsDto
{
    [JsonProperty("collections")]
    public IEnumerable<JToken> Collections { get; set; }

    [JsonProperty("links")]
    public IEnumerable<StacLinkDto> Links { get; set; }
}
