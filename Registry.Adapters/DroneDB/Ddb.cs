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
    private static readonly FileSystem FileSystem = new();
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
            var fullPath = GetLocalPath(inputPath);
            return _ddbWrapper.GenerateMemoryTile(fullPath, tz, tx, ty, retina ? 512 : 256, true, false,
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

    private static string NormalizePath(string path) => path.Replace('\\', '/');

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
            var fullPath = path != null ? GetLocalPath(path) : DatasetFolderPath;

            var entries = _ddbWrapper.List(DatasetFolderPath, fullPath, recursive);

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
            var fullPath = GetLocalPath(path);

            _ddbWrapper.Remove(DatasetFolderPath, fullPath);
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
            _ddbWrapper.MoveEntry(DatasetFolderPath, NormalizePath(source), NormalizePath(dest));
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
            _ddbWrapper.Build(DatasetFolderPath, path != null ? NormalizePath(path) : null, dest, force);
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
        var fullPath = Path.Combine(DatasetFolderPath, IDDB.DatabaseFolderName, IDDB.TmpFolderName, path);
        if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public bool IsBuildable(string path)
    {
        try
        {
            return _ddbWrapper.IsBuildable(DatasetFolderPath, NormalizePath(path));
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
            return _ddbWrapper.IsBuildActive(DatasetFolderPath, NormalizePath(path));
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

        return entry ?? throw new InvalidOperationException("Cannot get ddb info of dataset");
    }

    public byte[] GenerateThumbnail(string imagePath, int size)
    {
        try
        {
            var fullPath = GetLocalPath(imagePath);
            return _ddbWrapper.GenerateThumbnail(fullPath, size);
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
            var fullPath = GetLocalPath(path);
            _ddbWrapper.Add(DatasetFolderPath, fullPath);
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
                folderPath = GetLocalPath(path);

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
                {
                    if (!FileSystem.SafeFolderDelete(folderPath))
                        Debug.WriteLine($"Cannot delete folder '{folderPath}'");
                }
            }
        }
        else
        {
            string? filePath = null;

            try
            {
                filePath = GetLocalPath(path);

                stream.SafeReset();

                FileSystem.EnsureParentFolderExists(filePath);

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
                {
                    if (!FileSystem.SafeDelete(filePath))
                        Debug.WriteLine($"Cannot delete file '{filePath}'");
                }
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
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.GetRasterInfo(fullPath);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot get raster info of '{path}'", ex);
        }
    }

    public string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null)
    {
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.GetRasterMetadata(fullPath, formula, bandFilter);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot get raster metadata of '{path}'", ex);
        }
    }

    public byte[] GenerateThumbnailEx(string imagePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null)
    {
        try
        {
            var fullPath = GetLocalPath(imagePath);
            return _ddbWrapper.GenerateThumbnailEx(fullPath, size, preset, bands, formula, bandFilter,
                colormap, rescale);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException(
                $"Cannot generate thumbnail ex of '{imagePath}' with size '{size}'", ex);
        }
    }

    public byte[] GenerateTileEx(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        try
        {
            var fullPath = GetLocalPath(inputPath);
            return _ddbWrapper.GenerateMemoryTileEx(fullPath, tz, tx, ty, retina ? 512 : 256, true, false,
                inputPathHash, preset, bands, formula, bandFilter, colormap, rescale);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot generate tile ex of '{inputPath}'", ex);
        }
    }

    public string ValidateMergeMultispectral(string[] paths)
    {
        try
        {
            var fullPaths = paths.Select(GetLocalPath).ToArray();
            return _ddbWrapper.ValidateMergeMultispectral(fullPaths);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException("Cannot validate merge multispectral", ex);
        }
    }

    public byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512)
    {
        try
        {
            var fullPaths = paths.Select(GetLocalPath).ToArray();
            return _ddbWrapper.PreviewMergeMultispectral(fullPaths, previewBands, thumbSize);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException("Cannot preview merge multispectral", ex);
        }
    }

    public void MergeMultispectral(string[] paths, string outputCog)
    {
        try
        {
            var fullPaths = paths.Select(GetLocalPath).ToArray();
            var fullOutputCog = GetLocalPath(outputCog);
            _ddbWrapper.MergeMultispectral(fullPaths, fullOutputCog);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot merge multispectral: {ex.Message}", ex);
        }
    }

    public void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null)
    {
        try
        {
            var fullInputPath = GetLocalPath(inputPath);
            _ddbWrapper.ExportRaster(fullInputPath, outputPath, preset, bands, formula, bandFilter, colormap, rescale);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot export raster '{inputPath}'", ex);
        }
    }

    public string GetRasterValueInfo(string path)
    {
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.GetRasterValueInfo(fullPath);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot get raster value info of '{path}'", ex);
        }
    }

    public string GetRasterPointValue(string path, int x, int y)
    {
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.GetRasterPointValue(fullPath, x, y);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot get raster point value of '{path}'", ex);
        }
    }

    public string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1)
    {
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.GetRasterAreaStats(fullPath, x0, y0, x1, y1);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot get raster area stats of '{path}'", ex);
        }
    }

    public string GetRasterProfile(string path, string geoJsonLineString, int samples)
    {
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.GetRasterProfile(fullPath, geoJsonLineString, samples);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot get raster profile of '{path}'", ex);
        }
    }

    public string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation)
    {
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.CalculateVolume(fullPath, polygonGeoJson, baseMethod, flatElevation);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot calculate volume of '{path}'", ex);
        }
    }

    public string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity)
    {
        try
        {
            var fullPath = GetLocalPath(path);
            return _ddbWrapper.DetectStockpile(fullPath, lat, lon, radiusMeters, sensitivity);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot detect stockpile on '{path}'", ex);
        }
    }

    public void MaskBorders(string input, string output, int nearDist = 15, bool white = false)
    {
        try
        {
            var fullInput = GetLocalPath(input);
            var fullOutput = GetLocalPath(output);
            _ddbWrapper.MaskBorders(fullInput, fullOutput, nearDist, white);
        }
        catch (DdbException ex)
        {
            throw new InvalidOperationException($"Cannot mask borders of '{input}'", ex);
        }
    }
}