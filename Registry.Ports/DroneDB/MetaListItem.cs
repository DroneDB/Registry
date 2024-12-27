using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

public class MetaListItem
{
    [JsonProperty("count")]
    public int Count { get; set; }

    [JsonProperty("key")]
    public string Key { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; }
}