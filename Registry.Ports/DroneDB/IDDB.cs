using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Registry.Common.Model;

namespace Registry.Ports.DroneDB;

public interface IDDB
{
    /// <summary>
    /// DroneDB client version
    /// </summary>
    string Version { get; }

    /// <summary>
    /// DroneDB dataset folder path
    /// </summary>
    string DatasetFolderPath { get; }

    /// <summary>
    /// The build folder path
    /// </summary>
    string BuildFolderPath { get; }

    IEnumerable<Entry> Search(string path, bool recursive = false);
    void Add(string path, byte[] data);
    void Add(string path, Stream? data = null);
    void AddRaw(string path);

    void Remove(string path);
    void Move(string source, string dest);

    byte[] GenerateThumbnail(string imagePath, int size);
    byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash);

    void Init();

    Entry GetInfo();

    /// <summary>
    /// Calls DDB info command on specified path
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    Entry GetInfo(string path);

    string GetLocalPath(string path);

    /// <summary>
    /// Gets the specified path inside the DDB database
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    Entry? GetEntry(string path);

    bool EntryExists(string path);
    void Build(string path, string dest = null, bool force = false);
    void BuildAll(string dest = null, bool force = false);
    void BuildPending(string dest = null, bool force = false);

    public string GetTmpFolder(string path);
    bool IsBuildable(string path);
    bool IsBuildActive(string path);
    bool IsBuildPending();

    IMetaManager Meta { get; }
    long GetSize();
    Stamp GetStamp();

    JToken GetStac(string id, string stacCollectionRoot, string stacCatalogRoot, string path = null);

    // These consts are like magic strings: if anything changes this goes kaboom!
    public const string DatabaseFolderName = ".ddb";
    public const string BuildFolderName = "build";
    public const string TmpFolderName = "tmp";
}