using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB;

namespace Registry.Web.Test.Adapters;

public class MockMeta : IMetaManager
{
    private readonly Dictionary<string, string> _data = new();

    public Meta Add(string key, string data, string path = null)
    {
        throw new NotImplementedException();
    }

    public Meta Set(string key, string data, string path = null)
    {
        var fullKey = path != null ? $"{path}:{key}" : key;
        _data[fullKey] = data;

        return new Meta
        {
            Data = JToken.FromObject(data),
            Id = Guid.NewGuid().ToString(),
            ModifiedTime = DateTime.Now
        };
    }

    public int Remove(string id)
    {
        throw new NotImplementedException();
    }

    public JToken Get(string key, string path = null)
    {
        var fullKey = path != null ? $"{path}:{key}" : key;
        return _data.TryGetValue(fullKey, out var value) ? JToken.FromObject(value) : null;
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
        var fullKey = path != null ? $"{path}:{key}" : key;

        if (!_data.TryGetValue(fullKey, out var value))
            return default;

        // Handle type conversions
        if (typeof(T) == typeof(bool))
        {
            return (T)(object)(value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        if (typeof(T) == typeof(int))
        {
            return int.TryParse(value, out var intValue) ? (T)(object)intValue : default;
        }

        return JsonConvert.DeserializeObject<T>(value);
    }
}