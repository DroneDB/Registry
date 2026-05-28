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

    public byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate, string inputPathHash, string outputFormat);

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

    /// <summary>
    /// Cleans up a dataset by removing index entries whose underlying files no
    /// longer exist on disk and orphaned build artifacts.
    /// </summary>
    /// <param name="ddbPath">Path to the DroneDB database (parent of ".ddb")</param>
    /// <returns>Lists of removed entry paths and build artifact hashes</returns>
    public DdbCleanupResult Cleanup(string ddbPath);

    public Meta MetaAdd(string ddbPath, string key, string data, string? path = null);

    public Meta MetaSet(string ddbPath, string key, string data, string? path = null);

    public int MetaRemove(string ddbPath, string id);

    public string? MetaGet(string ddbPath, string key, string? path = null);

    public int MetaUnset(string ddbPath, string key, string? path = null);

    public List<MetaListItem> MetaList(string ddbPath, string? path = null);

    public List<MetaDump> MetaDump(string ddbPath, string? ids = null);

    public JToken Stac(string ddbPath, string? entry, string stacCollectionRoot, string id,
        string stacCatalogRoot);

    /// <summary>
    /// Rescans all files in the index to update metadata
    /// </summary>
    /// <param name="ddbPath">Path to the DroneDB database</param>
    /// <param name="types">Comma-separated list of entry types to rescan (e.g., "image,geoimage,pointcloud"), or null/empty for all</param>
    /// <param name="stopOnError">Whether to stop processing on first error</param>
    /// <returns>List of rescan results for each processed entry</returns>
    public List<RescanResult> RescanIndex(string ddbPath, string? types = null, bool stopOnError = true);

    void RegisterProcess(bool verbose = false);

    string TileMimeType { get; }
    string ThumbnailMimeType { get; }

    /// <summary>
    /// Get raster info including bands, detected sensor, and presets
    /// </summary>
    public string GetRasterInfo(string path);

    /// <summary>
    /// Get raster statistics and histogram for a band or formula
    /// </summary>
    public string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null);

    /// <summary>
    /// Generate thumbnail with extended visualization params
    /// </summary>
    public byte[] GenerateThumbnailEx(string filePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null);

    /// <summary>
    /// Generate tile with extended visualization params
    /// </summary>
    public byte[] GenerateMemoryTileEx(string inputPath, int tz, int tx, int ty,
        int tileSize, bool tms, bool forceRecreate, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Validate merge-multispectral inputs
    /// </summary>
    public string ValidateMergeMultispectral(string[] paths);

    /// <summary>
    /// Preview merge-multispectral result
    /// </summary>
    public byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512);

    /// <summary>
    /// Merge single-band rasters into multi-band COG
    /// </summary>
    public void MergeMultispectral(string[] paths, string outputCog);

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF
    /// </summary>
    public void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Get raster value info (min/max/unit/dimensions), including thermal calibration if applicable
    /// </summary>
    public string GetRasterValueInfo(string path);

    /// <summary>
    /// Get raster value (temperature/elevation/etc.) at a specific pixel location
    /// </summary>
    public string GetRasterPointValue(string path, int x, int y);

    /// <summary>
    /// Get raster value statistics for a rectangular area
    /// </summary>
    public string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1);

    /// <summary>
    /// Sample raster values along a GeoJSON LineString (WGS84). Returns a JSON
    /// profile with equispaced samples suitable for elevation/temperature charts.
    /// </summary>
    /// <param name="path">Path to the raster</param>
    /// <param name="geoJsonLineString">GeoJSON LineString geometry (WGS84)</param>
    /// <param name="samples">Requested number of equispaced samples (clamped 2..4096)</param>
    public string GetRasterProfile(string path, string geoJsonLineString, int samples);

    /// <summary>
    /// Calculate stockpile volume (cut/fill/net) over a polygon on a DEM raster.
    /// </summary>
    /// <param name="path">Path to the elevation raster</param>
    /// <param name="polygonGeoJson">GeoJSON Polygon or MultiPolygon (WGS84)</param>
    /// <param name="baseMethod">One of lowest_perimeter, average_perimeter, best_fit, flat</param>
    /// <param name="flatElevation">Elevation used when baseMethod = flat</param>
    public string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation);

    /// <summary>
    /// Auto-detect a stockpile footprint starting from a click on the raster.
    /// </summary>
    /// <param name="path">Path to the elevation raster</param>
    /// <param name="lat">Click latitude (WGS84)</param>
    /// <param name="lon">Click longitude (WGS84)</param>
    /// <param name="radiusMeters">Search radius (meters)</param>
    /// <param name="sensitivity">Detail level in [0,1]</param>
    public string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity);

    /// <summary>
    /// Auto-detect ALL stockpile footprints by full-DEM scan.
    /// </summary>
    /// <param name="path">Path to the elevation raster</param>
    /// <param name="sensitivity">Detail level in [0,1]</param>
    /// <param name="minAreaM2">Minimum component area in square meters (>=0)</param>
    /// <param name="maxResults">Maximum number of stockpiles to return (capped at 500)</param>
    public string DetectAllStockpiles(string path, float sensitivity, double minAreaM2, int maxResults);

    /// <summary>
    /// Generate contour lines from a single-band elevation raster (DEM/DSM/DTM).
    /// Returns a GeoJSON FeatureCollection of LineString features with an `elev` property (WGS84).
    /// Either <paramref name="interval"/> or <paramref name="count"/> must be provided.
    /// </summary>
    /// <param name="path">Path to the raster</param>
    /// <param name="interval">Vertical interval between contour levels (raster units). Null to derive from <paramref name="count"/>.</param>
    /// <param name="count">Target number of contour levels. Null when <paramref name="interval"/> is set.</param>
    /// <param name="baseOffset">Reference base elevation for level alignment</param>
    /// <param name="minElev">Drop contours below this elevation; null disables the bound</param>
    /// <param name="maxElev">Drop contours above this elevation; null disables the bound</param>
    /// <param name="simplifyTolerance">Geometry simplification tolerance in raster CRS units (0 = none)</param>
    /// <param name="bandIndex">1-based raster band index</param>
    public string GenerateContours(string path,
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
    public void MaskBorders(string input, string output, int nearDist = 15, bool white = false);

    // =====================================================================
    // OGC services support (raster region rendering + vector query/describe).
    // =====================================================================

    /// <summary>
    /// Render a geographic region of a georeferenced raster to a compressed
    /// image buffer (PNG / JPEG / WebP). Used by WMS GetMap and WMTS.
    /// </summary>
    /// <param name="inputPath">Path to the source raster.</param>
    /// <param name="bbox">[minX, minY, maxX, maxY] in <paramref name="bboxSrs"/>.</param>
    /// <param name="bboxSrs">CRS authority code (e.g. "EPSG:4326"). Empty/null defaults to EPSG:4326.</param>
    /// <param name="width">Output width in pixels (1..4096).</param>
    /// <param name="height">Output height in pixels (1..4096).</param>
    /// <param name="format">MIME type ("image/png", "image/jpeg", "image/webp")
    /// or shortcut ("png"/"jpeg"/"webp").</param>
    /// <param name="bands">Optional 1-based band selection (WCS RangeSubset).
    /// When provided the output retains exactly these bands in the requested
    /// order and no alpha channel is appended.</param>
    /// <param name="outputCrs">Optional target CRS authority code (WCS OutputCRS).
    /// Null/empty means "same as <paramref name="bboxSrs"/>" (legacy behaviour).</param>
    /// <returns>Encoded image bytes.</returns>
    public byte[] RenderRasterRegion(string inputPath, double[] bbox, string bboxSrs,
                                     int width, int height, string format,
                                     int[]? bands = null, string? outputCrs = null);

    /// <summary>
    /// Render a spectral index (NDVI / NDRE / NDWI / EVI / SAVI) over a raster
    /// region and apply a red-yellow-green color ramp. Used by WMS STYLES.
    /// </summary>
    /// <param name="inputPath">Path to the multi-band source raster.</param>
    /// <param name="indexName">One of NDVI / NDRE / NDWI / EVI / SAVI (case-insensitive).</param>
    /// <param name="bbox">[minX, minY, maxX, maxY] in <paramref name="bboxSrs"/>.</param>
    /// <param name="bboxSrs">CRS authority code; null/empty defaults to "EPSG:4326".</param>
    /// <param name="width">Output width (1..4096).</param>
    /// <param name="height">Output height (1..4096).</param>
    /// <param name="format">MIME type or shortcut ("image/png" / "png" / etc.).</param>
    public byte[] RenderRasterIndex(string inputPath, string indexName, double[] bbox,
                                    string bboxSrs, int width, int height, string format);

    /// <summary>
    /// Sample a georeferenced raster at a geographic point (WMS GetFeatureInfo /
    /// OGC API Coverages point access).
    /// </summary>
    /// <param name="inputPath">Path to the source raster.</param>
    /// <param name="x">X coordinate in <paramref name="srs"/>.</param>
    /// <param name="y">Y coordinate in <paramref name="srs"/>.</param>
    /// <param name="srs">CRS authority code (defaults to "EPSG:4326").</param>
    /// <returns>JSON string with bands, pixel, lon and lat fields.</returns>
    public string QueryRasterPoint(string inputPath, double x, double y, string? srs = null);

    /// <summary>
    /// Query features from a vector dataset (WFS GetFeature / OGC API Items).
    /// </summary>
    /// <param name="vectorPath">Path to the vector source (typically vec/source.gpkg).</param>
    /// <param name="layerName">Layer to query; null uses the first layer.</param>
    /// <param name="bbox">[minX,minY,maxX,maxY] spatial filter in <paramref name="bboxSrs"/>, or null.</param>
    /// <param name="bboxSrs">CRS of <paramref name="bbox"/> (e.g. "EPSG:4326"); null when bbox is null.</param>
    /// <param name="maxFeatures">Maximum features to return (clamped to [1,10000]; 0 → default 1000).</param>
    /// <param name="startIndex">0-based feature offset for pagination.</param>
    /// <param name="outputFormat">"application/json" (RFC7946 GeoJSON, default) or "application/gml+xml".</param>
    /// <returns>Encoded features as a string.</returns>
    public string QueryVector(string vectorPath, string? layerName = null,
                              double[]? bbox = null, string? bboxSrs = null,
                              int maxFeatures = 1000, int startIndex = 0,
                              string outputFormat = "application/json");

    /// <summary>
    /// Describe a vector dataset (WFS DescribeFeatureType / OGC API collection).
    /// </summary>
    /// <param name="vectorPath">Path to the vector source.</param>
    /// <param name="layerName">Layer to describe; null = all layers.</param>
    /// <returns>JSON describing driver, layers, fields, geometryType, srs, extent and feature count.</returns>
    public string DescribeVector(string vectorPath, string? layerName = null);
}