using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Web.Models.DTO;

public class StampDto
{
    [JsonProperty("checksum")]
    public string Checksum { get; set; }

    [JsonProperty("entries")]
    public List<Dictionary<string,string>> Entries { get; set; }

    [JsonProperty("meta")]
    public List<string> Meta { get; set; }

}