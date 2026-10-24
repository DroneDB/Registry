using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Registry.Web.Models.DTO;

public class StampDto
{
    [JsonPropertyName("checksum")]
    public string Checksum { get; set; }

    [JsonPropertyName("entries")]
    public List<Dictionary<string,string>> Entries { get; set; }

    [JsonPropertyName("meta")]
    public List<string> Meta { get; set; }

}