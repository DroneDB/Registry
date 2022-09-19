using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Adapters.DroneDB
{
    public class MetaManager : IMetaManager
    {
        private readonly IDDB _ddb;
        
        public MetaManager(IDDB ddb)
        {
            _ddb = ddb;
        }

        public Meta Add(string key, string data, string path = null)
        {

            var m = DDBWrapper.MetaAdd(_ddb.DatasetFolderPath, key, data, path);

            return new Meta
            {
                Data = JToken.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public Meta Set(string key, string data, string path = null)
        {
            var m = DDBWrapper.MetaSet(_ddb.DatasetFolderPath, key, data, path);

            return new Meta
            {
                Data = JToken.FromObject(m.Data),
                Id = m.Id,
                ModifiedTime = m.ModifiedTime
            };
        }

        public int Remove(string id)
        {
            return DDBWrapper.MetaRemove(_ddb.DatasetFolderPath, id);
        }

        public JToken Get(string key, string path = null)
        {
            var m = DDBWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);
            return JsonConvert.DeserializeObject<JToken>(m);
        }

        public T? Get<T>(string key, string path = null)
        {
            var m = DDBWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);
            var obj = JsonConvert.DeserializeObject<Meta>(m);

            return obj != null ? obj.Data.ToObject<T>() : default;
        }

        public int Unset(string key, string path = null)
        {
            return DDBWrapper.MetaUnset(_ddb.DatasetFolderPath, key, path);
        }

        public MetaListItem[] List(string path = null)
        {
            return DDBWrapper.MetaList(_ddb.DatasetFolderPath, path).ToArray();
        }

        public MetaDump[] Dump(string ids = null)
        {
            return DDBWrapper.MetaDump(_ddb.DatasetFolderPath, ids).ToArray();
        }
    }
}