namespace Registry.Ports.DroneDB.Models
{
    public class AddAction
    {
        public string Path { get;  }
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
}