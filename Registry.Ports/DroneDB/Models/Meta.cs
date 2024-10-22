using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;

namespace Registry.Ports.DroneDB.Models;

// Let's duplicate MetaDto for no apparent reason
public class Meta
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("data")]
    public JToken Data { get; set; }

    [JsonProperty("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }
}