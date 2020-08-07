using System;
using GeoJSON.Net;
using Newtonsoft.Json.Linq;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbObject
    {
        public int Id { get; set; }
        public string Path { get; set; }
        public DateTime ModifiedTime { get; set; }
        public string Hash { get; set; }
        public int Depth { get; set; }
        public int Size { get; set; }
        public int Type { get; set; }
        public JObject Meta { get; set; }
        public GeoJSONObject PointGeometry { get; set; }
        public GeoJSONObject PolygonGeometry { get; set; }
    }

}
