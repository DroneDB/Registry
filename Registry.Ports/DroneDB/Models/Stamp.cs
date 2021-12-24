using Newtonsoft.Json;
using System.Collections.Generic;

namespace Registry.Ports.DroneDB.Models
{
    // Let's duplicate StampDto for no apparent reason
    public class Stamp
    {
        [JsonProperty("checksum")]
        public string Checksum { get; set; }

        [JsonProperty("entries")]
        public List<Dictionary<string,string>> Entries { get; set; }

    }

}