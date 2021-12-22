using Newtonsoft.Json;

namespace Registry.Adapters.DroneDB.Models
{
    public class RemoveAction
    {
        [JsonProperty("path")]
        public string Path { get; }

        [JsonProperty("hash")]
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
}