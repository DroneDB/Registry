#nullable disable
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

public class StacLinkDto
{
    // href rel title
    [JsonPropertyName("href")]
    public string Href { get; set; }

    [JsonPropertyName("rel")]
    public string Relationship { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Title { get; set; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Method { get; set; }
}


public class StacItemDto
{
    
}