using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Models.DTO;

// Let's replicate MetaDump for no apparent reason
public class MetaDumpDto : MetaDto
{
    [JsonProperty("path")]
    public string Path { get; set; }

    [JsonProperty("key")]
    public string Key { get; set; }
}