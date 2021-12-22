using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GeoJSON.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Common;

namespace Registry.Web.Models.DTO
{
    public class EntryGeoDto : EntryDto
    {
        
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("point_geom")]
        public GeoJSONObject PointGeometry { get; set; }
        [JsonProperty("polygon_geom")]
        public GeoJSONObject PolygonGeometry { get; set; }
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
