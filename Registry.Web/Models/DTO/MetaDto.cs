using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;

namespace Registry.Web.Models.DTO
{
    public class MetaDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("data")]
        public JToken Data { get; set; }

        [JsonProperty("mtime")]
        [JsonConverter(typeof(SecondEpochConverter))]
        public DateTime ModifiedTime { get; set; }
    }
}