using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

// Let's duplicate StampDto for no apparent reason
public class Stamp
{
    [JsonProperty("checksum")]
    public string Checksum { get; set; }

    [JsonProperty("entries")]
    public List<Dictionary<string, string>> Entries { get; set; }

    [JsonProperty("meta")]
    public List<string> Meta { get; set; }



}