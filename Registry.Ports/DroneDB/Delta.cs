using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

public class Delta
{
    [JsonPropertyName("adds")]
    public AddAction[] Adds { get; set; }

    [JsonPropertyName("removes")]
    public RemoveAction[] Removes { get; set; }

    [JsonPropertyName("metaAdds")]
    public string[] MetaAdds { get; set; }
        
    [JsonPropertyName("metaRemoves")]
    public string[] MetaRemoves { get; set; }


}