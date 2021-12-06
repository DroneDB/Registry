using System;
using System.Collections.Generic;
using DDB.Bindings.Model;
using GeoJSON.Net.Feature;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbInfo
    {
        [JsonProperty("meta")]
        public Dictionary<string, string> Meta { get; set; }
        [JsonProperty("mtime")]
        public DateTime ModifiedTime { get; set; }
        [JsonProperty("path")]
        public string Path { get; set; }
        [JsonProperty("point_geom")]
        public Feature PointGeometry { get; set; }
        [JsonProperty("polygon_geom")]
        public Feature PolygonGeometry { get; set; }
        [JsonProperty("size")]
        public int Size { get; set; }
        [JsonProperty("type")]
        public EntryType Type { get; set; }
    }

    //public class Meta
    //{
    //    public float captureTime { get; set; }
    //    public float focalLength { get; set; }
    //    public float focalLength35 { get; set; }
    //    public int height { get; set; }
    //    public string make { get; set; }
    //    public string model { get; set; }
    //    public int orientation { get; set; }
    //    public string sensor { get; set; }
    //    public float sensorHeight { get; set; }
    //    public float sensorWidth { get; set; }
    //    public int width { get; set; }
    //}


}
