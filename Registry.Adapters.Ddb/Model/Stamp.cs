using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Adapters.Ddb.Model
{

    public class Stamp
    {
        [JsonProperty("checksum")]
        public string Checksum { get; set; }

        [JsonProperty("entries")]
        public List<Dictionary<string,string>> Entries { get; set; }

    }

}