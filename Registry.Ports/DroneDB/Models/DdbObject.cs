using System;
using GeoJSON.Net;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using Newtonsoft.Json.Linq;
using Registry.Common;

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
        public EntryType Type { get; set; }
        public JObject Meta { get; set; }
        public Point PointGeometry { get; set; }
        public Feature PolygonGeometry { get; set; }
    }

}
