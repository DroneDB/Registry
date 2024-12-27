using Newtonsoft.Json;

namespace Registry.Ports.DroneDB;

public class MetaDump : Meta
{
    [JsonProperty("path")]
    public string Path { get; set; }

    [JsonProperty("key")]
    public string Key { get; set; }

}