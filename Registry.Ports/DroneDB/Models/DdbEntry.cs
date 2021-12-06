using System;
using System.Collections.Generic;
using DDB.Bindings.Model;
using GeoJSON.Net;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using Newtonsoft.Json.Linq;
using Registry.Common;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbEntry
    {
        public int Id { get; set; }
        public string Path { get; set; }
        public DateTime ModifiedTime { get; set; }
        public string Hash { get; set; }
        public int Depth { get; set; }
        public long Size { get; set; }
        public EntryType Type { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public Point PointGeometry { get; set; }
        public Polygon PolygonGeometry { get; set; }
    }

}
