using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

// Let's replicate MetaDump for no apparent reason
public class MetaDumpDto : MetaDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; }
}