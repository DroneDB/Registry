#nullable disable
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

public class MetaListItemDto
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }
}