using Registry.Adapters.Ddb.Model;

namespace DDB.Bindings.Model
{
    public class Delta
    {
        public AddAction[] Adds { get; set; }
        public CopyAction[] Copies { get; set; }
        public RemoveAction[] Removes { get; set; }
    }
}