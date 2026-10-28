#nullable disable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

public class StacCatalogDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; }
    
    [JsonPropertyName("stac_version")]
    public string StacVersion { get; set; }
    
    [JsonPropertyName("id")]
    public string Id { get; set; }
    
    [JsonPropertyName("description")]
    public string Description { get; set; }
    
    [JsonPropertyName("links")]
    public IEnumerable<StacLinkDto> Links { get; set; }

    [JsonPropertyName("conformsTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<string> ConformsTo { get; set; }
}