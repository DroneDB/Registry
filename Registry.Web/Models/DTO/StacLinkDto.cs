using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class StacLinkDto
{
    // href rel title
    [JsonProperty("href")]
    public string Href { get; set; }
    
    [JsonProperty("rel")]
    public string Relationship { get; set; }
    
    [JsonProperty("title")]
    public string Title { get; set; }
}


public class StacItemDto
{
    
}