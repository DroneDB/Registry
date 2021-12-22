using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Adapters.DroneDB.Models
{

    public class Stamp
    {
        [JsonProperty("checksum")]
        public string Checksum { get; set; }

        [JsonProperty("entries")]
        public List<Dictionary<string,string>> Entries { get; set; }

    }

}