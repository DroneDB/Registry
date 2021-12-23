using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Models.DTO
{

    public class EntryDto
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public EntryType Type { get; set; }

        public Dictionary<string, object> Properties { get; set; }

        [JsonProperty("mtime")]
        [JsonConverter(typeof(SecondEpochConverter))]
        public DateTime ModifiedTime { get; set; }

        public long Size { get; set; }
        public int Depth { get; set; }

        [JsonProperty("point_geom")]
        public JObject PointGeometry { get; set; }

        [JsonProperty("polygon_geom")]
        public JObject PolygonGeometry { get; set; }
    }

    // Unix seconds, with decimal places for millisecond precision
}
