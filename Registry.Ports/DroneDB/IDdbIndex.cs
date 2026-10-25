using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Index read/write operations: search, add, remove, move, dataset identity/paths and STAC
/// export. The narrowest role interface for code that only reads or writes the index (e.g.
/// <c>IDatasetIndexQueue</c>).
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

    /// <summary>
    /// The entries selected by <paramref name="query"/>, sorted by path. DroneDB filters by
    /// folder, name and type in the index query, so prefer this to filtering the whole listing.
    /// </summary>
    IReadOnlyList<Entry> Find(EntryQuery query);

    /// <summary>
    /// <see cref="Find"/> without properties, geometries and meta, for callers that only need
    /// paths, hashes, types or sizes.
    /// </summary>
    IReadOnlyList<EntrySummary> FindSummaries(EntryQuery query);
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

    /// <summary>
    /// <see cref="AddRawBatchWithOptions(IReadOnlyList{string},bool)"/> for files whose hash was
    /// computed while they were written: a hint that still matches the file saves reading it again.
    /// </summary>
    BatchAddResult AddRawBatchWithOptions(IReadOnlyList<AddItem> items, bool stopOnError = false);

    void Remove(string path);
    void Move(string source, string dest);

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
    /// <param name="options">What to rescan; null rescans every entry and stops on the first error</param>
    /// <returns>List of rescan results for each processed entry</returns>
    List<RescanResult> RescanIndex(RescanOptions? options = null);

    /// <summary>
    /// The data-only STAC Item of <paramref name="path"/>, or the dataset's Collection when it is null
    /// (<see cref="IDdbWrapper.StacData"/>): Registry adds every id, link and href.
    /// </summary>
    JsonElement GetStacData(string path = null);

    /// <summary>
    /// The data-only STAC ItemCollection of the dataset (<see cref="IDdbWrapper.StacItemCollectionData"/>),
    /// optionally filtered by bbox and datetime, with paging.
    /// </summary>
    /// <param name="bbox">Optional bounding box "minX,minY,maxX,maxY" (WGS84), or null</param>
    /// <param name="datetime">Optional single RFC3339 datetime or interval "start/end" (".." for open ends), or null</param>
    /// <param name="limit">Maximum number of items to return</param>
    /// <param name="offset">Number of items to skip (paging)</param>
    JsonElement GetStacItemCollectionData(string bbox = null, string datetime = null, int limit = 10, int offset = 0);
}
