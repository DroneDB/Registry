using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Registry.Ports.Json;

namespace Registry.Ports.DroneDB;

public class Entry : EntrySummary
{
    /// <summary>Absent when the entry has no properties.</summary>
    [JsonPropertyName("properties")]
    [JsonConverter(typeof(SharedDocumentMapConverter))]
    public Dictionary<string, JsonElement>? Properties { get; set; }

    [JsonPropertyName("point_geom")]
    public JsonElement? PointGeometry { get; set; }

    [JsonPropertyName("polygon_geom")]
    public JsonElement? PolygonGeometry { get; set; }
}
