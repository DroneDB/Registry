using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Models.DTO
{

    public class EntryDto
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("hash")]
        public string Hash { get; set; }

        [JsonProperty("type")]
        public EntryType Type { get; set; }

        [JsonProperty("properties")]
        public Dictionary<string, object> Properties { get; set; }

        [JsonProperty("mtime")]
        [JsonConverter(typeof(SecondEpochConverter))]
        public DateTime ModifiedTime { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("depth")]
        public int Depth { get; set; }

        [JsonProperty("point_geom")]
        public JObject PointGeometry { get; set; }

        [JsonProperty("polygon_geom")]
        public JObject PolygonGeometry { get; set; }
    }
}
