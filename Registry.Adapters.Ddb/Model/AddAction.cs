using Registry.Adapters.Ddb.Model;

namespace DDB.Bindings.Model
{
    public class AddAction
    {
        public string Path { get;  }
        public EntryType Type { get;  }

        public AddAction(string path, EntryType type = EntryType.Generic)
        {
            Path = path;
            Type = type;
        }

        public override string ToString()
        {
            return $"ADD -> [{(Type == EntryType.Directory ? 'D' : 'F')}] {Path}";
        }
    }
}