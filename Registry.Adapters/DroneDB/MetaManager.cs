using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

public class MetaManager : IMetaManager
{
    private readonly IDDB _ddb;
    private readonly IDdbWrapper _ddbWrapper;

    public MetaManager(IDDB ddb, IDdbWrapper ddbWrapper)
    {
        _ddb = ddb;
        _ddbWrapper = ddbWrapper;
    }

    public Meta Add(string key, string data, string? path = null)
    {

        var m = _ddbWrapper.MetaAdd(_ddb.DatasetFolderPath, key, data, path);

        return new Meta
        {
            Data = JToken.FromObject(m.Data),
            Id = m.Id,
            ModifiedTime = m.ModifiedTime
        };
    }

    public Meta Set(string key, string data, string? path = null)
    {
        var m = _ddbWrapper.MetaSet(_ddb.DatasetFolderPath, key, data, path);

        return new Meta
        {
            Data = JToken.FromObject(m.Data),
            Id = m.Id,
            ModifiedTime = m.ModifiedTime
        };
    }

    public int Remove(string id)
    {
        return _ddbWrapper.MetaRemove(_ddb.DatasetFolderPath, id);
    }

    public JToken Get(string key, string? path = null)
    {
        var m = _ddbWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);
        return JsonConvert.DeserializeObject<JToken>(m);
    }

    public T? Get<T>(string key, string? path = null)
    {
        var m = _ddbWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);
        var obj = JsonConvert.DeserializeObject<Meta>(m);

        return obj != null ? obj.Data.ToObject<T>() : default;
    }

    public int Unset(string key, string? path = null)
    {
        return _ddbWrapper.MetaUnset(_ddb.DatasetFolderPath, key, path);
    }

    public MetaListItem[] List(string? path = null)
    {
        return _ddbWrapper.MetaList(_ddb.DatasetFolderPath, path).ToArray();
    }

    public MetaDump[] Dump(string? ids = null)
    {
        return _ddbWrapper.MetaDump(_ddb.DatasetFolderPath, ids).ToArray();
    }
}