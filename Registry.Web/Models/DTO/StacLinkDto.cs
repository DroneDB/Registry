using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class StacLinkDto
{
    // href rel title
    [JsonProperty("href")]
    public string Href { get; set; }

    [JsonProperty("rel")]
    public string Relationship { get; set; }

    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)]
    public string Type { get; set; }

    [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)]
    public string Title { get; set; }

    [JsonProperty("method", NullValueHandling = NullValueHandling.Ignore)]
    public string Method { get; set; }
}


public class StacItemDto
{
    
}