using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Adapters.DroneDB
{
    public class DdbMetaManager : IDdbMetaManager
    {
        private readonly IDdb _ddb;
        
        public DdbMetaManager(IDdb ddb)
        {
            _ddb = ddb;
        }

        public DdbMeta Add(string key, string data, string path = null)
        {

            var m = DDB.Bindings.DroneDB.MetaAdd(_ddb.DatabaseFolder, key, data, path);

            return new DdbMeta
            {
                Data = JObject.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public DdbMeta Set(string key, string data, string path = null)
        {
            var m = DDB.Bindings.DroneDB.MetaSet(_ddb.DatabaseFolder, key, data, path);

            return new DdbMeta
            {
                Data = JObject.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public int Remove(string id)
        {
            return DDB.Bindings.DroneDB.MetaRemove(_ddb.DatabaseFolder, id);
        }

        public string Get(string key, string path = null)
        {

            var m = DDB.Bindings.DroneDB.MetaGet(_ddb.DatabaseFolder, key, path);

            return m;
        }

        public int Unset(string key, string path = null)
        {
            return DDB.Bindings.DroneDB.MetaUnset(_ddb.DatabaseFolder, key, path);
        }

        public DdbMetaListItem[] List(string path = null)
        {

            var list = DDB.Bindings.DroneDB.MetaList(_ddb.DatabaseFolder, path);

            return list.Select(item => new DdbMetaListItem
            {
                Count = item.Count,
                Key = item.Key,
                Path = item.Path
            }).ToArray();
            
        }
    }
}