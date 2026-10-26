using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Text.Json;
using Newtonsoft.Json;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// Main DroneDB adapter implementing <see cref="IDDB"/> (init, add, remove, info, build, clone, etc.).
/// </summary>
public class DDB : IDDB
{
    private static readonly FileSystem FileSystem = new();
    private readonly IDdbWrapper _wrapper;

    // Null only in a DDB read from job data stored by earlier releases: that one only names its folder
    private IDdbWrapper Wrapper => _wrapper ?? throw new InvalidOperationException(
        $"The dataset '{DatasetFolderPath}' was read from job data: it only names its folder");

    [JsonIgnore] public string Version => Wrapper.GetVersion();

    [JsonProperty] public string DatasetFolderPath { get; private set; }

    /// <summary>libddb's build folder under the database folder (the adapter's knowledge, not Registry's).</summary>
    public const string BuildFolderName = "build";

    /// <summary>The build folder (also the member job data stored by earlier releases carries).</summary>
    [JsonProperty] public string BuildFolderPath { get; private set; }

    [JsonIgnore] public IReadOnlyList<string> DerivedFolderPaths => [BuildFolderPath];

    [JsonIgnore] public IMetaManager Meta { get; private set; }

    [JsonConstructor]
    private DDB()
    {
        //
    }

    /// <summary>
    /// A DDB read from Hangfire job data stored by earlier releases: the job shims read only
    /// <see cref="DatasetFolderPath"/> and open the dataset through DI, so no native wrapper is
    /// created here and any other member throws.
    /// </summary>
    [OnDeserialized]
    internal void OnDeserializedMethod(StreamingContext context)
    {
        BuildFolderPath = Path.Combine(DatasetFolderPath, IDDB.DatabaseFolderName, BuildFolderName);
    }

    public DDB(string ddbPath, IDdbWrapper ddbWrapper)
    {
        if (string.IsNullOrWhiteSpace(ddbPath))
            throw new ArgumentException("Path should not be null or empty");

        _wrapper = ddbWrapper;

        if (!Directory.Exists(ddbPath))
            throw new ArgumentException($"Path '{ddbPath}' does not exist");

        DatasetFolderPath = ddbPath;
        BuildFolderPath = Path.Combine(ddbPath, IDDB.DatabaseFolderName, BuildFolderName);
        Meta = new MetaManager(this, ddbWrapper);
    }

    public byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash, string outputFormat)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(inputPath);
            return Wrapper.GenerateMemoryTile(fullPath, tz, tx, ty, retina ? 512 : 256, true, false,
                inputPathHash, outputFormat);
        }, ex => $"Cannot generate tile of '{inputPath}' (format={outputFormat})");
    }

    public void Init()
    {
        Wrap(() =>
        {
            var res = Wrapper.Init(DatasetFolderPath);
            Debug.WriteLine(res);
        }, ex => $"Cannot initialize ddb in folder '{DatasetFolderPath}'");
    }

    public bool IsEmpty()
    {
        // The dataset root info counts the entries (SELECT COUNT)
        var properties = GetInfo().Properties;
        if (properties == null || !properties.TryGetValue("entries", out var entries) || !entries.TryGetInt64(out var count))
            throw new InvalidOperationException($"Cannot count the entries of ddb '{DatasetFolderPath}'");

        return count == 0;
    }

    public bool Checkpoint() => Wrapper.Checkpoint(DatasetFolderPath);

    public string? GetIndexFingerprint()
    {
        try
        {
            var database = new FileInfo(Path.Combine(DatasetFolderPath, IDDB.DatabaseFolderName, IDDB.DatabaseFileName));
            if (!database.Exists)
                return null;

            // A commit appends to the WAL (size and write time change); a checkpoint rewrites the database
            var wal = new FileInfo(database.FullName + "-wal");
            return $"{database.LastWriteTimeUtc.Ticks}:{database.Length}|" +
                   (wal.Exists ? $"{wal.LastWriteTimeUtc.Ticks}:{wal.Length}" : "-");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string GetLocalPath(string path)
    {
        return CommonUtils.SafeCombine(DatasetFolderPath, path);
    }

    /// <summary>
    /// Dataset-relative form of <paramref name="path"/>, resolved like the native listing
    /// ("./a", "a/../b", "dir/"): "" for the dataset root, null outside the dataset.
    /// </summary>
    private string? ToDatasetRelative(string? path)
    {
        var local = GetLocalPath(path ?? string.Empty);
        var relativePath = Path.GetRelativePath(DatasetFolderPath, local).ToDdbPath().TrimEnd('/');
        if (relativePath is "." or "")
            return string.Empty;
        // Outside the dataset (a ".." escape, another drive): nothing there is indexed
        return CommonUtils.IsUnder(DatasetFolderPath, local) ? relativePath : null;
    }

    public Entry? GetEntry(string path)
    {
        // The dataset root has no entry of its own and nothing outside the dataset is indexed
        var relativePath = ToDatasetRelative(path);
        if (string.IsNullOrEmpty(relativePath))
            return null;

        return Wrap(() => Wrapper.GetEntry(DatasetFolderPath, relativePath),
            ex => $"Cannot get '{path}' from ddb '{DatasetFolderPath}'");
    }

    public bool EntryExists(string path)
    {
        return GetEntry(path) != null;
    }

    public IReadOnlyList<Entry> Find(EntryQuery query) => FindWith(query, Wrapper.ListEntries);

    public IReadOnlyList<EntrySummary> FindSummaries(EntryQuery query) =>
        FindWith(query, Wrapper.ListEntrySummaries);

    private List<T> FindWith<T>(EntryQuery query, Func<string, EntryQuery, List<T>> list)
    {
        ArgumentNullException.ThrowIfNull(query);
        var folder = ToDatasetRelative(query.Path) ??
                     throw new ArgumentException($"Path '{query.Path}' is outside the dataset", nameof(query));
        return Wrap(() => list(DatasetFolderPath, query with { Path = folder }),
            ex => $"Cannot list '{query.Path}' in ddb '{DatasetFolderPath}'");
    }

    public void Remove(string path)
    {
        Wrap(() =>
        {
            var fullPath = GetLocalPath(path);

            Wrapper.Remove(DatasetFolderPath, fullPath);
        }, ex => $"Cannot remove '{path}' from ddb '{DatasetFolderPath}'");
    }

    public void Move(string source, string dest)
    {
        Wrap(() => Wrapper.MoveEntry(DatasetFolderPath, source.ToDdbPath(), dest.ToDdbPath()),
            ex => $"Cannot move '{source}' to {dest} from ddb '{DatasetFolderPath}'");
    }

    public void Build(string path, string dest = null, bool force = false) =>
        RunBuild(path, dest, force, null);

    public void BuildWithOptions(string path, BuildOptions options, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        RunBuild(path, null, force, options);
    }

    private void RunBuild(string path, string dest, bool force, BuildOptions options)
    {
        Wrap(() => Wrapper.Build(DatasetFolderPath, path?.ToDdbPath(), dest, force, options: options),
            ex => $"Cannot build '{path}' from ddb '{DatasetFolderPath}'");
    }

    public void BuildPending(string dest = null, bool force = false)
    {
        Wrap(() => Wrapper.Build(DatasetFolderPath, null, dest, force, true),
            ex => $"Cannot build pending from ddb '{DatasetFolderPath}'");
    }

    public string GetTmpFolder(string path)
    {
        var fullPath = Path.Combine(DatasetFolderPath, IDDB.DatabaseFolderName, IDDB.TmpFolderName, path);
        if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public bool IsBuildPending()
    {
        return Wrap(() => Wrapper.IsBuildPending(DatasetFolderPath),
            ex => $"Cannot call IsBuildPending from ddb '{DatasetFolderPath}'");
    }

    public IReadOnlyList<EntryBuildArtifacts> GetBuildArtifacts(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
            return [];

        var normalized = paths.Select(p => p.ToDdbPath()).ToArray();
        return Wrap(() => Wrapper.GetBuildArtifacts(DatasetFolderPath, normalized),
            ex => $"Cannot read the build manifest from ddb '{DatasetFolderPath}'");
    }

    public string? ResolveBuildFile(string hash, string path)
    {
        return Wrap(() => Wrapper.ResolveBuildFile(DatasetFolderPath, hash, path.ToDdbPath()),
            ex => $"Cannot resolve the build file '{path}' of '{hash}' in ddb '{DatasetFolderPath}'");
    }

    public CopiedBuildArtifacts CopyBuildArtifactsTo(string destinationDatasetFolder, string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDatasetFolder);
        return Wrap(() => Wrapper.CopyBuildArtifacts(DatasetFolderPath, destinationDatasetFolder, hash),
            ex => $"Cannot copy the build '{hash}' from ddb '{DatasetFolderPath}' to '{destinationDatasetFolder}'");
    }

    public PendingBuildInfo[] GetPendingBuildInfo()
    {
        return Wrap(() => Wrapper.GetPendingBuildInfo(DatasetFolderPath),
            ex => $"Cannot call GetPendingBuildInfo from ddb '{DatasetFolderPath}'");
    }

    public DdbCleanupResult Cleanup()
    {
        return Wrap(() => Wrapper.Cleanup(DatasetFolderPath),
            ex => $"Cannot call Cleanup from ddb '{DatasetFolderPath}'");
    }

    public Entry GetInfo()
    {
        return GetInfo(DatasetFolderPath);
    }

    public Entry GetInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or empty");

        var info = Wrapper.Info(DatasetFolderPath);

        var entry = info.FirstOrDefault();

        return entry ?? throw new InvalidOperationException("Cannot get ddb info of dataset");
    }

    public byte[] GenerateThumbnail(string imagePath, int size)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(imagePath);
            return Wrapper.GenerateThumbnail(fullPath, size);
        }, ex => $"Cannot generate thumbnail of '{imagePath}' with size '{size}'");
    }

    public void AddRaw(string path)
    {
        Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            Wrapper.Add(DatasetFolderPath, fullPath);
        }, ex => $"Cannot add '{path}' to ddb '{DatasetFolderPath}'");
    }

    public IReadOnlyList<Entry> AddRawBatch(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0)
            return Array.Empty<Entry>();

        return Wrap(() =>
        {
            // Resolve all dataset-relative paths to local absolute paths and index them
            // in a single native call: the C++ side wraps the whole batch in one
            // transaction (one connection open) instead of one-per-file.
            var fullPaths = new string[paths.Count];
            for (var i = 0; i < paths.Count; i++)
                fullPaths[i] = GetLocalPath(paths[i]);

            return Wrapper.Add(DatasetFolderPath, fullPaths);
        }, ex => $"Cannot batch-add {paths.Count} file(s) to ddb '{DatasetFolderPath}'");
    }

    public BatchAddResult AddRawBatchWithOptions(IReadOnlyList<string> paths, bool stopOnError = false) =>
        AddRawBatchWithOptions(paths.Select(p => new AddItem(p)).ToArray(), stopOnError);

    public BatchAddResult AddRawBatchWithOptions(IReadOnlyList<AddItem> items, bool stopOnError = false)
    {
        if (items == null || items.Count == 0)
            return new BatchAddResult();

        return Wrap(() =>
        {
            var files = new AddItem[items.Count];
            for (var i = 0; i < items.Count; i++)
                files[i] = items[i] with { Path = GetLocalPath(items[i].Path) };

            return Wrapper.AddWithHints(DatasetFolderPath, files, stopOnError);
        }, ex => $"Cannot batch-add {items.Count} file(s) (with options) to ddb '{DatasetFolderPath}'");
    }

    public void Add(string path, Stream? stream = null)
    {
        var localPath = GetLocalPath(path);
        try
        {
            if (stream == null)
            {
                Directory.CreateDirectory(localPath);
                Wrap(() => Wrapper.Add(DatasetFolderPath, localPath),
                    ex => $"Cannot add folder '{path}' to ddb '{DatasetFolderPath}'");
            }
            else
            {
                stream.SafeReset();
                FileSystem.EnsureParentFolderExists(localPath);

                using (var writer = File.OpenWrite(localPath))
                {
                    stream.CopyTo(writer);
                }

                Wrap(() => Wrapper.Add(DatasetFolderPath, localPath),
                    ex => $"Cannot add '{path}' to ddb '{DatasetFolderPath}'");
            }
        }
        finally
        {
            FileSystem.TryDelete(localPath, ex => Debug.WriteLine($"Cannot delete '{localPath}': {ex.Message}"));
        }
    }


    public override string ToString()
    {
        return DatasetFolderPath;
    }

    /// <summary>
    /// Runs a libddb call: a generic native failure (a plain <see cref="DdbException"/>) becomes an
    /// <see cref="InvalidOperationException"/> with the call's context, as it always did; every
    /// typed outcome passes through unchanged: the transient ones (<see cref="DdbBusyException"/>,
    /// <see cref="DdbBuildInProgressException"/>, <see cref="DdbRetryableException"/>,
    /// <see cref="DdbShutdownException"/>) for the retry policies and the 503 path,
    /// <see cref="DdbInvalidArgumentsException"/> (400), <see cref="DdbNotFoundException"/> (404)
    /// and cancellation.
    /// </summary>
    private static T Wrap<T>(Func<T> call, Func<DdbException, string> message)
    {
        try
        {
            return call();
        }
        catch (DdbException ex) when (ex.GetType() == typeof(DdbException))
        {
            throw new InvalidOperationException(message(ex), ex);
        }
    }

    /// <inheritdoc cref="Wrap{T}"/>
    private static void Wrap(Action call, Func<DdbException, string> message)
    {
        try
        {
            call();
        }
        catch (DdbException ex) when (ex.GetType() == typeof(DdbException))
        {
            throw new InvalidOperationException(message(ex), ex);
        }
    }

    public Stamp GetStamp()
    {
        return Wrapper.GetStamp(DatasetFolderPath);
    }

    public JsonElement GetStacData(string path = null) => Wrapper.StacData(DatasetFolderPath, path);

    public JsonElement GetStacItemCollectionData(string bbox = null, string datetime = null, int limit = 10,
        int offset = 0) =>
        Wrapper.StacItemCollectionData(DatasetFolderPath, bbox, datetime, limit, offset);

    public List<RescanResult> RescanIndex(RescanOptions? options = null)
    {
        return Wrap(() => Wrapper.RescanIndex(DatasetFolderPath, options),
            ex => $"Cannot rescan index in folder '{DatasetFolderPath}'");
    }

    public void ClearBuildCache()
    {
        if (Directory.Exists(BuildFolderPath))
        {
            Directory.Delete(BuildFolderPath, true);
        }

        Directory.CreateDirectory(BuildFolderPath);
    }

    public string GetRasterInfo(string path)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.GetRasterInfo(fullPath);
        }, ex => $"Cannot get raster info of '{path}'");
    }

    public string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.GetRasterMetadata(fullPath, formula, bandFilter);
        }, ex => $"Cannot get raster metadata of '{path}'");
    }

    public byte[] GenerateThumbnailEx(string imagePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(imagePath);
            return Wrapper.GenerateThumbnailEx(fullPath, size, preset, bands, formula, bandFilter,
                colormap, rescale);
        }, ex => $"Cannot generate thumbnail ex of '{imagePath}' with size '{size}'");
    }

    public byte[] GenerateTileEx(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(inputPath);
            return Wrapper.GenerateMemoryTileEx(fullPath, tz, tx, ty, retina ? 512 : 256, true, false,
                inputPathHash, preset, bands, formula, bandFilter, colormap, rescale);
        }, ex => $"Cannot generate tile ex of '{inputPath}'");
    }

    public string ValidateMergeMultispectral(string[] paths)
    {
        return Wrap(() =>
        {
            var fullPaths = paths.Select(GetLocalPath).ToArray();
            return Wrapper.ValidateMergeMultispectral(fullPaths);
        }, ex => "Cannot validate merge multispectral");
    }

    public byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512)
    {
        return Wrap(() =>
        {
            var fullPaths = paths.Select(GetLocalPath).ToArray();
            return Wrapper.PreviewMergeMultispectral(fullPaths, previewBands, thumbSize);
        }, ex => "Cannot preview merge multispectral");
    }

    public void MergeMultispectral(string[] paths, string outputCog)
    {
        Wrap(() =>
        {
            var fullPaths = paths.Select(GetLocalPath).ToArray();
            var fullOutputCog = GetLocalPath(outputCog);
            Wrapper.MergeMultispectral(fullPaths, fullOutputCog);
        }, ex => $"Cannot merge multispectral: {ex.Message}");
    }

    public string ValidateAlignRaster(string sourcePath, string referencePath)
    {
        return Wrap(() => Wrapper.ValidateAlignRaster(GetLocalPath(sourcePath), GetLocalPath(referencePath)),
            ex => "Cannot validate align raster");
    }

    public string AlignRaster(string sourcePath, string referencePath, string outputPath, string mode = "similarity")
    {
        return Wrap(() =>
        {
            return Wrapper.AlignRaster(GetLocalPath(sourcePath), GetLocalPath(referencePath),
                GetLocalPath(outputPath), mode);
        }, ex => $"Cannot align raster: {ex.Message}");
    }

    public void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        Wrap(() =>
        {
            var fullInputPath = GetLocalPath(inputPath);
            Wrapper.ExportRaster(fullInputPath, outputPath, preset, bands, formula, bandFilter, colormap, rescale);
        }, ex => $"Cannot export raster '{inputPath}'");
    }

    public void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale, int tileSize,
        Action<double, string?>? progress, CancellationToken cancellationToken)
    {
        // A cancellation (DdbCanceledException) is not a DdbException: it passes through
        Wrap(() => Wrapper.ExportRaster(GetLocalPath(inputPath), outputPath, preset, bands, formula, bandFilter,
                colormap, rescale, tileSize, progress, cancellationToken),
            ex => $"Cannot export raster '{inputPath}'");
    }

    public string GetRasterValueInfo(string path)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.GetRasterValueInfo(fullPath);
        }, ex => "Could not read raster value info.");
    }

    public string GetRasterPointValue(string path, int x, int y)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.GetRasterPointValue(fullPath, x, y);
        }, ex => "Could not read raster value at the requested pixel.");
    }

    public string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.GetRasterAreaStats(fullPath, x0, y0, x1, y1);
        }, ex => "Could not compute raster area statistics.");
    }

    public string GetRasterProfile(string path, string geoJsonLineString, int samples)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.GetRasterProfile(fullPath, geoJsonLineString, samples);
        }, ex => "Could not compute raster profile for the requested line.");
    }

    public string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.CalculateVolume(fullPath, polygonGeoJson, baseMethod, flatElevation);
        }, ex => "Cannot calculate volume for this area. Make sure the polygon falls inside the raster and contains valid elevation data.");
    }

    public string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.DetectStockpile(fullPath, lat, lon, radiusMeters, sensitivity);
        }, ex => "No stockpile detected at the selected location. Try clicking closer to a clearly elevated area, increase the search radius, or lower the sensitivity.");
    }

    public string DetectAllStockpiles(string path, float sensitivity, double minAreaM2, int maxResults)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.DetectAllStockpiles(fullPath, sensitivity, minAreaM2, maxResults);
        }, ex => "No stockpiles could be detected on this raster. Try lowering the sensitivity or reducing the minimum area.");
    }

    public string GenerateContours(string path,
                                   double? interval,
                                   int? count,
                                   double baseOffset = 0.0,
                                   double? minElev = null,
                                   double? maxElev = null,
                                   double simplifyTolerance = 0.0,
                                   int bandIndex = 1)
    {
        return Wrap(() =>
        {
            var fullPath = GetLocalPath(path);
            return Wrapper.GenerateContours(fullPath, interval, count, baseOffset,
                                                minElev, maxElev, simplifyTolerance, bandIndex);
        }, ex => "Could not generate contour lines for this raster.");
    }

    public void MaskBorders(string input, string output, int nearDist = 15, bool white = false)
    {
        Wrap(() =>
        {
            var fullInput = GetLocalPath(input);
            var fullOutput = GetLocalPath(output);
            Wrapper.MaskBorders(fullInput, fullOutput, nearDist, white);
        }, ex => $"Cannot mask borders of '{input}'");
    }
}