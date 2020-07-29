using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Registry.Web.Models.DTO
{
    public class ObjectDto
    {
        
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("path")]
        public string Path { get; set; }
        [JsonProperty("cdate")]
        public int Creationdate { get; set; }
        [JsonProperty("mdate")]
        public int ModifiedTime { get; set; }
        [JsonProperty("hash")]
        public string Hash { get; set; }
        [JsonProperty("depth")]
        public int Depth { get; set; }
        [JsonProperty("size")]
        public int Size { get; set; }
        [JsonProperty("type")]
        public int Type { get; set; }
        [JsonProperty("meta")]
        public JObject Meta { get; set; }
        [JsonProperty("point_geom")]
        public JObject PointGeometry { get; set; }
        [JsonProperty("polygon_geom")]
        public JObject PoligonGeometry { get; set; }
    }

    /*
    public class Meta
    {
        public float cameraPitch { get; set; }
        public float cameraRoll { get; set; }
        public float cameraYaw { get; set; }
        public float captureTime { get; set; }
        public float focalLength { get; set; }
        public float focalLength35 { get; set; }
        public int height { get; set; }
        public string make { get; set; }
        public string model { get; set; }
        public int orientation { get; set; }
        public string sensor { get; set; }
        public float sensorHeight { get; set; }
        public float sensorWidth { get; set; }
        public int width { get; set; }
    }*/



}
