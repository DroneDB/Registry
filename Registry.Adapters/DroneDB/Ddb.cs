using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Common.Model;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

public class DDB : IDDB
{
    private IDdbWrapper _ddbWrapper;

    [JsonIgnore] public string Version => _ddbWrapper.GetVersion();

    [JsonProperty] public string DatasetFolderPath { get; private set; }

    [JsonProperty] public string BuildFolderPath { get; private set; }

    [JsonIgnore] public IMetaManager Meta { get; private set; }

    [JsonConstructor]
    private DDB()
    {
        //
    }

    [OnDeserialized]
    internal void OnDeserializedMethod(StreamingContext context)
    {
        BuildFolderPath = Path.Combine(DatasetFolderPath, IDDB.DatabaseFolderName, IDDB.BuildFolderName);

        // NOTE: If this was deserialized it's from hangfire, so we need the native wrapper
        _ddbWrapper = new NativeDdbWrapper(false);
        Meta = new MetaManager(this, _ddbWrapper);
    }

    public DDB(string ddbPath, IDdbWrapper ddbWrapper)
    {
        if (string.IsNullOrWhiteSpace(ddbPath))
            throw new ArgumentException("Path should not be null or empty");

        _ddbWrapper = ddbWrapper;

        if (!Directory.Exists(ddbPath))
            throw new ArgumentException($"Path '{ddbPath}' does not exist");

        DatasetFolderPath = ddbPath;
        BuildFolderPath = Path.Combine(ddbPath, IDDB.DatabaseFolderName, IDDB.BuildFolderName);
        Meta = new MetaManager(this, ddbWrapper);
    }

    public byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash)
    {
        try
        {
            return _ddbWrapper.GenerateMemoryTile(inputPath, tz, tx, ty, retina ? 512 : 256, true, false,
                inputPathHash);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot generate tile of '{inputPath}'", ex);
        }
    }

    public void Init()
    {
        try
        {
            var res = _ddbWrapper.Init(DatasetFolderPath);
            Debug.WriteLine(res);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot initialize ddb in folder '{DatasetFolderPath}'", ex);
        }
    }


    public long GetSize()
    {
        return _ddbWrapper.Info(DatasetFolderPath).FirstOrDefault()?.Size ?? 0;
    }

    public string GetLocalPath(string path)
    {
        return CommonUtils.SafeCombine(DatasetFolderPath, path);
    }

    public Entry? GetEntry(string path)
    {
        var objs = Search(path, true).ToArray();

        if (objs.Any(p => p.Path == path))
            return objs.First();

        var parent = Path.GetDirectoryName(path);

        objs = Search(parent).ToArray();

        return objs.FirstOrDefault(item => item.Path == path);
    }

    public bool EntryExists(string path)
    {
        return GetEntry(path) != null;
    }


    public IEnumerable<Entry> Search(string path, bool recursive = false)
    {
        try
        {
            // If the path is not absolute let's rebase it on ddbPath
            if (path != null && !Path.IsPathRooted(path))
                path = Path.Combine(DatasetFolderPath, path);

            // If path is null we use the base ddb path
            path ??= DatasetFolderPath;

            var entries = _ddbWrapper.List(DatasetFolderPath, path, recursive);

            if (entries == null)
            {
                Debug.WriteLine("Strange null return value");
                return [];
            }

            return entries;
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot list '{path}' to ddb '{DatasetFolderPath}'", ex);
        }
    }

    public void Add(string path, byte[] data)
    {
        using var stream = new MemoryStream(data);
        Add(path, stream);
    }

    public void Remove(string path)
    {
        try
        {
            // If the path is not absolute let's rebase it on ddbPath
            if (!Path.IsPathRooted(path)) path = Path.Combine(DatasetFolderPath, path);

            _ddbWrapper.Remove(DatasetFolderPath, path);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot remove '{path}' from ddb '{DatasetFolderPath}'", ex);
        }
    }

    public void Move(string source, string dest)
    {
        try
        {
            _ddbWrapper.MoveEntry(DatasetFolderPath, source, dest);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot move '{source}' to {dest} from ddb '{DatasetFolderPath}'",
                ex);
        }
    }

    public void Build(string path, string dest = null, bool force = false)
    {
        try
        {
            _ddbWrapper.Build(DatasetFolderPath, path, dest, force);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot build '{path}' from ddb '{DatasetFolderPath}'", ex);
        }
    }

    public void BuildAll(string dest = null, bool force = false)
    {
        try
        {
            _ddbWrapper.Build(DatasetFolderPath, null, dest, force);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot build all from ddb '{DatasetFolderPath}'", ex);
        }
    }

    public void BuildPending(string dest = null, bool force = false)
    {
        try
        {
            _ddbWrapper.Build(DatasetFolderPath, null, dest, force, true);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot build pending from ddb '{DatasetFolderPath}'", ex);
        }
    }

    public string GetTmpFolder(string path)
    {
        string fullPath = Path.Combine(DatasetFolderPath, IDDB.DatabaseFolderName, IDDB.TmpFolderName, path);
        if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public bool IsBuildable(string path)
    {
        try
        {
            return _ddbWrapper.IsBuildable(DatasetFolderPath, path);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot call IsBuildable from ddb '{DatasetFolderPath}'", ex);
        }
    }

    public bool IsBuildActive(string path)
    {
        try
        {
            return _ddbWrapper.IsBuildActive(DatasetFolderPath, path);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot call IsBuildActive from ddb '{DatasetFolderPath}'", ex);
        }
    }

    public bool IsBuildPending()
    {
        try
        {
            return _ddbWrapper.IsBuildPending(DatasetFolderPath);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot call IsBuildPending from ddb '{DatasetFolderPath}'", ex);
        }
    }

    public Entry GetInfo()
    {
        return GetInfo(DatasetFolderPath);
    }

    public Entry GetInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or empty");

        var info = _ddbWrapper.Info(DatasetFolderPath);

        var entry = info.FirstOrDefault();

        if (entry == null)
            throw new InvalidOperationException("Cannot get ddb info of dataset");

        return entry;
    }

    public byte[] GenerateThumbnail(string imagePath, int size)
    {
        try
        {
            return _ddbWrapper.GenerateThumbnail(imagePath, size);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException(
                $"Cannot generate thumbnail of '{imagePath}' with size '{size}'", ex);
        }
    }

    public void AddRaw(string path)
    {
        try
        {
            _ddbWrapper.Add(DatasetFolderPath, path);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot add '{path}' to ddb '{DatasetFolderPath}'", ex);
        }
    }

    public void Add(string path, Stream? stream = null)
    {
        if (stream == null)
        {
            string? folderPath = null;

            try
            {
                folderPath = Path.Combine(DatasetFolderPath, path);

                Directory.CreateDirectory(folderPath);

                _ddbWrapper.Add(DatasetFolderPath, folderPath);
            }
            catch (DdbException ex)
            {
                throw new InvalidOperationException($"Cannot add folder '{path}' to ddb '{DatasetFolderPath}'", ex);
            }
            finally
            {
                if (folderPath != null && Directory.Exists(folderPath))
                    if (!CommonUtils.SafeDeleteFolder(folderPath))
                        Debug.WriteLine($"Cannot delete folder '{folderPath}'");
            }
        }
        else
        {
            string? filePath = null;

            try
            {
                filePath = Path.Combine(DatasetFolderPath, path);

                stream.SafeReset();

                CommonUtils.EnsureSafePath(filePath);

                using (var writer = File.OpenWrite(filePath))
                {
                    stream.CopyTo(writer);
                }

                _ddbWrapper.Add(DatasetFolderPath, filePath);
            }
            catch (DdbException ex)
            {
                throw new InvalidOperationException($"Cannot add '{path}' to ddb '{DatasetFolderPath}'", ex);
            }
            finally
            {
                if (filePath != null && File.Exists(filePath))
                    if (!CommonUtils.SafeDelete(filePath))
                        Debug.WriteLine($"Cannot delete file '{filePath}'");
            }
        }
    }


    public override string ToString()
    {
        return DatasetFolderPath;
    }

    public Stamp GetStamp()
    {
        return _ddbWrapper.GetStamp(DatasetFolderPath);
    }

    public JToken GetStac(string id, string stacCollectionRoot, string stacCatalogRoot, string path = null)
    {
        return _ddbWrapper.Stac(DatasetFolderPath, path, stacCollectionRoot, id, stacCatalogRoot);
    }

    public List<RescanResult> RescanIndex(string? types = null, bool stopOnError = true)
    {
        try
        {
            return _ddbWrapper.RescanIndex(DatasetFolderPath, types, stopOnError);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot rescan index in folder '{DatasetFolderPath}'", ex);
        }
    }
}