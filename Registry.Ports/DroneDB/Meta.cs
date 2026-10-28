using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

// Let's duplicate MetaDto for no apparent reason
public class Meta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("mtime")]
    [JsonConverter(typeof(SecondEpochConverter))]
    public DateTime ModifiedTime { get; set; }
}