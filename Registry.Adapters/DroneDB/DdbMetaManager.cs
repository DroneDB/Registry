using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.Ddb;
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

            var m = DroneDBWrapper.MetaAdd(_ddb.DatasetFolderPath, key, data, path);

            return new DdbMeta
            {
                Data = JToken.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public DdbMeta Set(string key, string data, string path = null)
        {
            var m = DroneDBWrapper.MetaSet(_ddb.DatasetFolderPath, key, data, path);

            return new DdbMeta
            {
                Data = JToken.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public int Remove(string id)
        {
            return DroneDBWrapper.MetaRemove(_ddb.DatasetFolderPath, id);
        }

        public string Get(string key, string path = null)
        {

            var m = DroneDBWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);

            return m;
        }

        public int Unset(string key, string path = null)
        {
            return DroneDBWrapper.MetaUnset(_ddb.DatasetFolderPath, key, path);
        }

        public DdbMetaListItem[] List(string path = null)
        {

            var list = DroneDBWrapper.MetaList(_ddb.DatasetFolderPath, path);

            return list.Select(item => new DdbMetaListItem
            {
                Count = item.Count,
                Key = item.Key,
                Path = item.Path
            }).ToArray();
            
        }
    }
}