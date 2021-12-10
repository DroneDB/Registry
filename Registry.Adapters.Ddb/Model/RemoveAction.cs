namespace Registry.Adapters.Ddb.Model
{
    public class RemoveAction
    {
        public string Path { get; }
        public EntryType Type { get; }

        public RemoveAction(string path, EntryType type = EntryType.Generic)
        {
            Path = path;
            Type = type;
        }
        public override string ToString()
        {
            return $"DEL -> [{(Type == EntryType.Directory ? 'D' : 'F')}] {Path}";
        }

    }
}