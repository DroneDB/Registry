using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Adapters.DroneDB.Models
{
    public class Meta
    {
        public string Id { get; set; }
        public JToken Data { get; set; }

        [JsonProperty("mtime")]
        [JsonConverter(typeof(SecondEpochConverter))]
        public DateTime ModifiedTime { get; set; }
    }
}