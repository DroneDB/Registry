#nullable disable
using System.Text.Json.Serialization;
using Registry.Ports.DroneDB;

namespace Registry.Web.Models.DTO;

public class DeltaDto
{
    [JsonPropertyName("adds")]
    public AddActionDto[] Adds { get; set; }

    [JsonPropertyName("removes")]
    public RemoveAction[] Removes { get; set; }

    public string[] MetaAdds { get; set; }

    [JsonPropertyName("metaRemoves")]
    public string[] MetaRemoves { get; set; }
}

public class AddActionDto
{
    [JsonPropertyName("path")]
    public string Path { get;  }

    [JsonPropertyName("hash")]
    public string Hash { get;  }

    public AddActionDto(string path, string hash)
    {
        Path = path;
        Hash = hash;
    }

    public override string ToString()
    {
        return $"ADD -> [{(string.IsNullOrEmpty(Hash) ? 'D' : 'F')}] {Path}";
    }
}

public class RemoveActionDto
{
    [JsonPropertyName("path")]
    public string Path { get; }

    [JsonPropertyName("hash")]
    public string Hash { get; }

    public RemoveActionDto(string path, string hash)
    {
        Path = path;
        Hash = hash;
    }
    public override string ToString()
    {
        return $"DEL -> [{(string.IsNullOrEmpty(Hash) ? 'D' : 'F')}] {Path}";
    }

}