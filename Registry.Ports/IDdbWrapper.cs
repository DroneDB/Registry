using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Registry.Ports.DroneDB;

namespace Registry.Ports;

public interface IDdbWrapper
{
    public string GetVersion();
    public string Init(string directory);

    public List<Entry> Add(string ddbPath, string path, bool recursive = false);

    public List<Entry> Add(string ddbPath, string[] paths, bool recursive = false);

    public void Remove(string ddbPath, string path);

    public void Remove(string ddbPath, string[] paths);

    public List<Entry> Info(string path, bool recursive = false, int maxRecursionDepth = 0, bool withHash = false);

    public List<Entry> Info(string[] paths, bool recursive = false, int maxRecursionDepth = 0, bool withHash = false);

    public List<Entry> List(string ddbPath, string path, bool recursive = false, int maxRecursionDepth = 0);
    public List<Entry> List(string ddbPath, string[] paths, bool recursive = false, int maxRecursionDepth = 0);

    public void AppendPassword(string ddbPath, string password);

    public bool VerifyPassword(string ddbPath, string password);

    public void ClearPasswords(string ddbPath);

    public Dictionary<string, object> ChangeAttributes(string ddbPath, Dictionary<string, object> attributes);

    public Dictionary<string, object> GetAttributes(string ddbPath);

    public void GenerateThumbnail(string filePath, int size, string destPath);

    public byte[] GenerateThumbnail(string filePath, int size);

    public string GenerateTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false);

    public byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate = false, string inputPathHash = "");

    public void SetTag(string ddbPath, string newTag);

    public string? GetTag(string ddbPath);

    public Stamp GetStamp(string ddbPath);

    public Delta Delta(string ddbPath, string ddbTarget);

    public List<string> ApplyDelta(Delta delta, string sourcePath, string ddbPath, MergeStrategy mergeStrategy,
        string? sourceMetaDump = null);

    public Delta Delta(Stamp source, Stamp target);
    public Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "");

    public void MoveEntry(string ddbPath, string source, string dest);

    public void Build(string ddbPath, string? source = null, string? dest = null, bool force = false,
        bool pendingOnly = false);

    public bool IsBuildable(string ddbPath, string path);

    public bool IsBuildActive(string ddbPath, string path);

    public bool IsBuildPending(string ddbPath);

    public Meta MetaAdd(string ddbPath, string key, string data, string? path = null);

    public Meta MetaSet(string ddbPath, string key, string data, string? path = null);

    public int MetaRemove(string ddbPath, string id);

    public string? MetaGet(string ddbPath, string key, string? path = null);

    public int MetaUnset(string ddbPath, string key, string? path = null);

    public List<MetaListItem> MetaList(string ddbPath, string? path = null);

    public List<MetaDump> MetaDump(string ddbPath, string? ids = null);

    public JToken Stac(string ddbPath, string? entry, string stacCollectionRoot, string id,
        string stacCatalogRoot);

    void RegisterProcess(bool verbose = false);

    string TileMimeType { get; }
    string ThumbnailMimeType { get; }
}