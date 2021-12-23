using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Web.Models.DTO
{
    public class MetaDto
    {
        public string Id { get; set; }
        public JToken Data { get; set; }

        [JsonProperty("mtime")]
        [JsonConverter(typeof(SecondEpochConverter))]
        public DateTime ModifiedTime { get; set; }
    }
}