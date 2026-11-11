using System.Text.Json.Serialization;

namespace Registry.Ports.DroneDB;

public class RemoveAction
{
    [JsonPropertyName("path")]
    public string Path { get; }

    [JsonPropertyName("hash")]
    public string Hash { get; }

    public RemoveAction(string path, string hash)
    {
        Path = path;
        Hash = hash;
    }
    public override string ToString()
    {
        return $"DEL -> [{(string.IsNullOrEmpty(Hash) ? 'D' : 'F')}] {Path}";
    }

}