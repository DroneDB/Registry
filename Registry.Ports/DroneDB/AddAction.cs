using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

// Let's copy AddActionDto for no reason
public class AddAction
{
    [JsonPropertyName("path")]
    public string Path { get;  }

    [JsonPropertyName("hash")]
    public string Hash { get;  }

    public AddAction(string path, string hash)
    {
        Path = path;
        Hash = hash;
    }

    public override string ToString()
    {
        return $"ADD -> [{(string.IsNullOrEmpty(Hash) ? 'D' : 'F')}] {Path}";
    }
}