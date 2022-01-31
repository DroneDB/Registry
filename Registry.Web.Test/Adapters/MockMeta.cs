using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Test.Adapters
{
    public class MockMeta : IMetaManager
    {
        
        private Dictionary<string, string> _meta = new Dictionary<string, string>();
        
        public Meta Add(string key, string data, string path = null)
        {
            throw new NotImplementedException();
        }

        public Meta Set(string key, string data, string path = null)
        {
            _meta.Add(key, data);
            return new Meta { Data = JToken.FromObject(data), Id = Guid.NewGuid().ToString(), ModifiedTime = DateTime.Now };
        }

        public int Remove(string id)
        {
            throw new System.NotImplementedException();
        }

        public string Get(string key, string path = null)
        {
            return _meta[key];
        }

        public int Unset(string key, string path = null)
        {
            throw new System.NotImplementedException();
        }

        public MetaListItem[] List(string path = null)
        {
            throw new System.NotImplementedException();
        }

        public MetaDump[] Dump(string ids = null)
        {
            throw new System.NotImplementedException();
        }
    }
}