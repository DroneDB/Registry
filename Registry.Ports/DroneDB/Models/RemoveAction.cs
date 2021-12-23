namespace Registry.Ports.DroneDB.Models
{
    public class RemoveAction
    {
        public string Path { get; }

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