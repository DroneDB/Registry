using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

public class MetaDump : Meta
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

}