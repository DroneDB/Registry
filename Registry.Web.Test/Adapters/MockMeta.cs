using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Test.Adapters
{
    public class MockMeta : IMetaManager
    {
        private readonly List<Tuple<string, Meta>> _metas = new();
        
        public Meta Add(string key, string data, string path = null)
        {
            throw new NotImplementedException();
        }

        public Meta Set(string key, string data, string path = null)
        {
            var meta = new Meta
                { Data = JToken.FromObject(data), Id = Guid.NewGuid().ToString(), ModifiedTime = DateTime.Now };
            _metas.Add(Tuple.Create(path, meta));
            return meta;
        }

        public int Remove(string id)
        {
            return _metas.RemoveAll(item => item.Item2.Id == id);
        }

        public JToken Get(string key, string path = null)
        {
            var item2 = _metas.FirstOrDefault(item => item.Item1 == path)?.Item2;
            return item2 != null ? JToken.FromObject(item2) : null;
        }

        public int Unset(string key, string path = null)
        {
            throw new NotImplementedException();
        }

        public MetaListItem[] List(string path = null)
        {
            throw new NotImplementedException();
        }

        public MetaDump[] Dump(string ids = null)
        {
            throw new NotImplementedException();
        }

        public T Get<T>(string key, string path = null)
        {
            var res = _metas.FirstOrDefault(m => m.Item1 == path);
            return res == null ? default : res.Item2.Data.ToObject<T>();
        }
    }
}