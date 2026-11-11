using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

public class MetaListItem
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}