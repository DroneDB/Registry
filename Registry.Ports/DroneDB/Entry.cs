using System.Collections.Generic;
using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

public class Entry : EntrySummary
{
    [JsonProperty("properties")]
    public Dictionary<string, object> Properties { get; set; }

    [JsonProperty("point_geom")]
    public object PointGeometry { get; set; }

    [JsonProperty("polygon_geom")]
    public object PolygonGeometry { get; set; }
}
