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

    /// <summary>
    /// Rescans all files in the index to update metadata
    /// </summary>
    /// <param name="types">Comma-separated list of entry types to rescan (e.g., "image,geoimage,pointcloud"), or null/empty for all</param>
    /// <param name="stopOnError">Whether to stop processing on first error</param>
    /// <returns>List of rescan results for each processed entry</returns>
    List<RescanResult> RescanIndex(string? types = null, bool stopOnError = true);

    /// <summary>
    /// Clears the build cache (thumbnails, tiles, COGs, etc.) for the dataset
    /// </summary>
    void ClearBuildCache();

    /// <summary>
    /// Get raster info including bands, detected sensor, and presets
    /// </summary>
    string GetRasterInfo(string path);

    /// <summary>
    /// Get raster statistics and histogram for a band or formula
    /// </summary>
    string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null);

    /// <summary>
    /// Generate thumbnail with extended visualization params
    /// </summary>
    byte[] GenerateThumbnailEx(string imagePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null);

    /// <summary>
    /// Generate tile with extended visualization params
    /// </summary>
    byte[] GenerateTileEx(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Validate merge-multispectral inputs
    /// </summary>
    string ValidateMergeMultispectral(string[] paths);

    /// <summary>
    /// Preview merge-multispectral result
    /// </summary>
    byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512);

    /// <summary>
    /// Merge single-band rasters into multi-band COG
    /// </summary>
    void MergeMultispectral(string[] paths, string outputCog);

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF
    /// </summary>
    void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Get raster value info (min/max/unit/dimensions), including thermal calibration if applicable
    /// </summary>
    string GetRasterValueInfo(string path);

    /// <summary>
    /// Get raster value (temperature/elevation/etc.) at a specific pixel location
    /// </summary>
    string GetRasterPointValue(string path, int x, int y);

    /// <summary>
    /// Get raster value statistics for a rectangular area
    /// </summary>
    string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1);

    /// <summary>
    /// Sample raster values along a GeoJSON LineString (WGS84)
    /// </summary>
    string GetRasterProfile(string path, string geoJsonLineString, int samples);

    /// <summary>
    /// Calculate stockpile volume (cut/fill/net) over a polygon on a DEM raster
    /// </summary>
    string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation);

    /// <summary>
    /// Auto-detect a stockpile footprint starting from a click on the raster
    /// </summary>
    string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity);

    /// <summary>
    /// Auto-detect ALL stockpile footprints by full-DEM scan.
    /// </summary>
    string DetectAllStockpiles(string path, float sensitivity, double minAreaM2, int maxResults);

    /// <summary>
    /// Generate contour lines from a single-band elevation raster (DEM/DSM/DTM).
    /// Returns a GeoJSON FeatureCollection of LineString features.
    /// </summary>
    string GenerateContours(string path,
                            double? interval,
                            int? count,
                            double baseOffset = 0.0,
                            double? minElev = null,
                            double? maxElev = null,
                            double simplifyTolerance = 0.0,
                            int bandIndex = 1);

    /// <summary>
    /// Mask orthophoto borders making them transparent
    /// </summary>
    void MaskBorders(string input, string output, int nearDist = 15, bool white = false);

    // These consts are like magic strings: if anything changes this goes kaboom!
    public const string DatabaseFolderName = ".ddb";
    public const string BuildFolderName = "build";
    public const string TmpFolderName = "tmp";
}