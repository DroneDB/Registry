using System.Text.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO;

// Let's replicate MetaDump for no apparent reason
public class MetaDumpDto : MetaDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; }
}