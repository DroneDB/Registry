using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Registry.Common;

namespace Registry.Web.Models.DTO
{
    public class EntryDto
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public EntryType Type { get; set; }
        public long Size { get; set; }
        public int Depth { get; set; }

        [JsonProperty("meta")]
        public Dictionary<string, object> Meta { get; set; }

        [JsonProperty("mtime")]
        [JsonConverter(typeof(UnixDateTimeConverter))] 
        public DateTime? ModifiedTime { get; set; }
    }
}
