using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Index read/write operations: search, add, remove, move, dataset identity/paths and STAC
/// export. The narrowest role interface for code that only reads or writes the index (e.g.
/// <c>IDatasetIndexQueue</c>) — see ImproveParallelWrites plan, workstream 04 §7.
/// </summary>
public interface IDdbIndex
{
    /// <summary>
    /// DroneDB client version
    /// </summary>
    string Version { get; }

    /// <summary>
    /// DroneDB dataset folder path
    /// </summary>
    string DatasetFolderPath { get; }

    IEnumerable<Entry> Search(string path, bool recursive = false);
    void Add(string path, byte[] data);
    void Add(string path, Stream? data = null);
    void AddRaw(string path);

    /// <summary>
    /// Indexes multiple already-on-disk files in a single native call (one database
    /// connection and one transaction for the whole batch), returning the resulting
    /// index entries (with type and hash).
    /// </summary>
    /// <param name="paths">Dataset-relative paths of the files to index.</param>
    /// <returns>The index entries produced for the supplied paths.</returns>
    IReadOnlyList<Entry> AddRawBatch(IReadOnlyList<string> paths);

    /// <summary>
    /// Batch add with the full completeness contract (entries/unchanged/errors) and,
    /// when <paramref name="stopOnError"/> is false, per-item error isolation: one corrupt
    /// file fails only itself instead of the whole batch. Used by <c>IDatasetIndexQueue</c>
    /// to match native results back to per-caller callers by path.
    /// </summary>
    BatchAddResult AddRawBatchWithOptions(IReadOnlyList<string> paths, bool stopOnError = false);

    void Remove(string path);
    void Move(string source, string dest);
    void Init();

    string GetLocalPath(string path);

    /// <summary>
    /// Gets the specified path inside the DDB database
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    Entry? GetEntry(string path);

    bool EntryExists(string path);

    /// <summary>
    /// Rescans all files in the index to update metadata
    /// </summary>
    /// <param name="types">Comma-separated list of entry types to rescan (e.g., "image,geoimage,pointcloud"), or null/empty for all</param>
    /// <param name="stopOnError">Whether to stop processing on first error</param>
    /// <returns>List of rescan results for each processed entry</returns>
    List<RescanResult> RescanIndex(string? types = null, bool stopOnError = true);

    JToken GetStac(string id, string stacCollectionRoot, string stacCatalogRoot, string path = null);

    /// <summary>
    /// Generates a STAC ItemCollection (GeoJSON FeatureCollection) for the dataset,
    /// optionally filtered by bbox and datetime, with paging.
    /// </summary>
    /// <param name="id">Collection id (e.g. "org/ds")</param>
    /// <param name="stacCollectionRoot">URL of the parent STAC collection</param>
    /// <param name="stacCatalogRoot">URL of the root STAC catalog</param>
    /// <param name="bbox">Optional bounding box "minX,minY,maxX,maxY" (WGS84), or null</param>
    /// <param name="datetime">Optional single RFC3339 datetime or interval "start/end" (".." for open ends), or null</param>
    /// <param name="limit">Maximum number of items to return</param>
    /// <param name="offset">Number of items to skip (paging)</param>
    JToken GetStacItemCollection(string id, string stacCollectionRoot, string stacCatalogRoot,
        string bbox = null, string datetime = null, int limit = 10, int offset = 0);
}
