using System;
using Newtonsoft.Json;

namespace Registry.Adapters.Ddb.Model
{
    public class Meta
    {
        public string Id { get; set; }
        public object Data { get; set; }

        [JsonProperty("mtime")]
        [JsonConverter(typeof(SecondEpochConverter))]
        public DateTime ModifiedTime { get; set; }
    }
}