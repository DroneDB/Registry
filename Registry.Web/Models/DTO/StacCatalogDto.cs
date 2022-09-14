using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class StacCatalogDto
{
    [JsonProperty("type")]
    public string Type { get; set; }
    
    [JsonProperty("stac_version")]
    public string StacVersion { get; set; }
    
    [JsonProperty("id")]
    public string Id { get; set; }
    
    [JsonProperty("description")]
    public string Description { get; set; }
    
    [JsonProperty("links")]
    public IEnumerable<StacLink> Links { get; set; }
}

public class StacLink
{
    // href rel title
    [JsonProperty("href")]
    public string Href { get; set; }
    
    [JsonProperty("rel")]
    public string Relationship { get; set; }
    
    [JsonProperty("title")]
    public string Title { get; set; }
}