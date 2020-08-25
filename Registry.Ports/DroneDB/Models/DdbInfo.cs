using GeoJSON.Net.Feature;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Ports.DroneDB.Models
{
    public class DdbInfo
    {
        [JsonProperty("meta")]
        public JObject Meta { get; set; }
        [JsonProperty("mtime")]
        public int ModifiedTime { get; set; }
        [JsonProperty("path")]
        public string Path { get; set; }
        [JsonProperty("point_geom")]
        public Feature PointGeometry { get; set; }
        [JsonProperty("polygon_geom")]
        public Feature PolygonGeometry { get; set; }
        [JsonProperty("size")]
        public int Size { get; set; }
        [JsonProperty("type")]
        public DdbInfoType Type { get; set; }
    }

    public enum DdbInfoType
    {
        Undefined = 0,
        Directory = 1,
        Generic = 2,
        GeoImage = 3,
        GeoRaster = 4,
        PointCloud = 5,
        Image = 6,
        DroneDb = 7
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
