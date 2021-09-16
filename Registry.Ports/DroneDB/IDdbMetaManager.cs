using Registry.Ports.DroneDB.Models;

namespace Registry.Ports.DroneDB
{
    public interface IDdbMetaManager
    {
        public DdbMeta Add(string key, string data, string path = null);
        public DdbMeta Set(string key, string data, string path = null);
        public int Remove(string id);
        public string Get(string key, string path = null);
        public int Unset(string key, string path = null);
        public DdbMetaListItem[] List(string path = null);

    }
}