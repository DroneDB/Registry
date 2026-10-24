using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Registry.Ports.Json;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// IMetaManager implementation via native DroneDB meta calls.
/// </summary>
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

        return _ddbWrapper.MetaAdd(_ddb.DatasetFolderPath, key, data, path);
    }

    public Meta Set(string key, string data, string? path = null)
    {
        return _ddbWrapper.MetaSet(_ddb.DatasetFolderPath, key, data, path);
    }

    public int Remove(string id)
    {
        return _ddbWrapper.MetaRemove(_ddb.DatasetFolderPath, id);
    }

    public JsonElement Get(string key, string? path = null)
    {
        var m = _ddbWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);
        return JsonSerializer.Deserialize<JsonElement>(m!, NativeJson.Options);
    }

    public T? Get<T>(string key, string? path = null)
    {
        var m = _ddbWrapper.MetaGet(_ddb.DatasetFolderPath, key, path);
        var obj = JsonSerializer.Deserialize<Meta>(m!, NativeJson.Options);

        return obj?.Data is { } data ? data.Deserialize<T>(NativeJson.Options) : default;
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