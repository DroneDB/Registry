using System.Collections.Generic;
using System.Text.Json;
using Registry.Ports.DroneDB;

namespace Registry.Ports;

public interface IDdbWrapper
{
    public string GetVersion();
    public string Init(string directory);

    public List<Entry> Add(string ddbPath, string path, bool recursive = false);

    public List<Entry> Add(string ddbPath, string[] paths, bool recursive = false);

    /// <summary>
    /// Adds files in one native call, each with the hash its writer computed (DDBAddWithHints): DroneDB
    /// uses a hint only while the file still has its size and write time, so a stale hint is never
    /// indexed. Paths are absolute; folders are not expanded. With <paramref name="stopOnError"/> false
    /// a file that fails only fails itself.
    /// </summary>
    public BatchAddResult AddWithHints(string ddbPath, IReadOnlyList<AddItem> files, bool stopOnError = false);

    public void Remove(string ddbPath, string path);

    public void Remove(string ddbPath, string[] paths);

    public List<Entry> Info(string path, bool recursive = false, int maxRecursionDepth = 0, bool withHash = false);

    /// <summary>
    /// Closes the idle pooled index connections of the datasets under a folder
    /// (DDBReleaseConnections). Returns how many were closed.
    /// </summary>
    public int ReleaseConnections(string path);

    /// <summary>
    /// Moves the committed changes of the index of a dataset folder from its WAL into the database
    /// file (DDBCheckpoint). False when another connection kept it from completing.
    /// </summary>
    public bool Checkpoint(string ddbPath);

    /// <summary>
    /// Exact lookup of one entry (file or folder), including its metadata.
    /// </summary>
    /// <param name="ddbPath">Dataset folder (parent of .ddb)</param>
    /// <param name="path">Dataset-relative path with forward slashes, no wildcards</param>
    /// <returns>The entry, or null if the index has no entry with this path</returns>
    public Entry? GetEntry(string ddbPath, string path);

    /// <summary>
    /// The entries selected by <paramref name="query"/> (filtered natively), sorted by path,
    /// with properties, geometries and meta.
    /// </summary>
    /// <param name="ddbPath">Dataset folder (parent of .ddb)</param>
    /// <param name="query">Dataset-relative folder with forward slashes, and filters</param>
    public List<Entry> ListEntries(string ddbPath, EntryQuery query);

    /// <summary>
    /// <see cref="ListEntries"/> without properties, geometries and meta: a fraction of the
    /// native work, payload and managed allocations.
    /// </summary>
    public List<EntrySummary> ListEntrySummaries(string ddbPath, EntryQuery query);

    public byte[] GenerateThumbnail(string filePath, int size);

    public byte[] GenerateMemoryTile(string inputPath, int tz, int tx, int ty, int tileSize, bool tms,
        bool forceRecreate, string inputPathHash, string outputFormat);

    public Stamp GetStamp(string ddbPath);

    /// <summary>
    /// The checksum of the dataset's stamp (<see cref="Stamp.Checksum"/>) without building the
    /// stamp (DDBGetStampChecksum).
    /// </summary>
    public string GetStampChecksum(string ddbPath);

    /// <summary>
    /// Applies <paramref name="delta"/> with the files it adds taken from <paramref name="sourcePath"/>, and returns
    /// the conflicts. For files whose hashes the caller checked against the delta while it received them
    /// (<c>DDBApplyDeltaEx</c>, ABI 2.0): libddb does not read those files again. <paramref name="verifiedHashes"/> maps a delta
    /// path (its exact spelling) to the lowercase hex SHA-256 the delta gives it; every other added file
    /// is hashed by libddb. An entry that is not a file the delta adds, or has another hash, fails the
    /// call (<see cref="DroneDB.DdbInvalidArgumentsException"/>) before anything changes.
    /// </summary>
    public List<string> ApplyDelta(Delta delta, string sourcePath, string ddbPath, MergeStrategy mergeStrategy,
        string? sourceMetaDump, IReadOnlyDictionary<string, string> verifiedHashes);

    public Delta Delta(Stamp source, Stamp target);
    public Dictionary<string, bool> ComputeDeltaLocals(Delta delta, string ddbPath, string hlDestFolder = "");

    public void MoveEntry(string ddbPath, string source, string dest);

    /// <param name="options">Limits of this build (DDBBuildEx); null: none, the plain DDBBuild.</param>
    public void Build(string ddbPath, string? source = null, string? dest = null, bool force = false,
        bool pendingOnly = false, BuildOptions? options = null);

    /// <summary>
    /// The build state and the existing artifacts of several entries with one index open
    /// (DDBGetBuildArtifacts): one element per path, in input order. libddb owns the build
    /// layout, the states and the type rules; Registry serves the paths it returns.
    /// </summary>
    /// <param name="ddbPath">Dataset folder (parent of .ddb)</param>
    /// <param name="paths">Dataset-relative entry paths (exact match)</param>
    public EntryBuildArtifacts[] GetBuildArtifacts(string ddbPath, string[] paths);

    /// <summary>
    /// A file of a build, checked by libddb (DDBResolveBuildFile): its absolute path and, for a declared artifact,
    /// the media type of the manifest. It serves <c>.../build/{hash}/{path}</c>, tiles included.
    /// </summary>
    /// <param name="ddbPath">Dataset folder (parent of .ddb)</param>
    /// <param name="hash">The hash of the build (<see cref="EntryBuild.Hash"/>)</param>
    /// <param name="path">The file, relative to the build root (an artifact path or a file below a directory artifact)</param>
    /// <returns>The file, or null when no build has this hash or the file is not one of its artifacts</returns>
    /// <exception cref="DdbInvalidArgumentsException">The hash is malformed or the path breaks the dataset path rules.</exception>
    public ResolvedBuildFile? ResolveBuildFile(string ddbPath, string hash, string path);

    /// <summary>The entry types of libddb and what it does with them (DDBGetEntryTypes).</summary>
    public IReadOnlyList<EntryTypeInfo> GetEntryTypes();

    /// <summary>
    /// libddb's dataset path rule (DDBNormalizePath): the normalized form of
    /// <paramref name="path"/> or the first rule it breaks. No dataset is opened.
    /// </summary>
    /// <param name="path">A dataset-relative path, '/' or '\' separated. A NUL byte or a lone
    /// surrogate, which the C API cannot receive, is rejected by the adapter itself
    /// (<see cref="NormalizedEntryPath.RejectUnmarshallable"/>).</param>
    /// <param name="reservedFirstSegments">Names reserved as the first segment only (Registry's
    /// ".uploads"); null or empty reserves none. ".ddb" is always reserved, at any depth.</param>
    /// <exception cref="System.ArgumentException">A reserved name is not a single segment.</exception>
    public NormalizedEntryPath NormalizePath(string path, IReadOnlyCollection<string>? reservedFirstSegments = null);

    /// <summary>
    /// Copies the build of an entry (its artifacts and pending marker) to another dataset
    /// (DDBCopyBuildArtifacts), so a copied or moved file needs no rebuild.
    /// </summary>
    /// <exception cref="DdbNotFoundException">No buildable entry of the source has this hash.</exception>
    /// <exception cref="DdbBuildInProgressException">A build of it runs in either dataset (nothing copied).</exception>
    /// <exception cref="DdbRetryableException">The destination's disk is (nearly) full.</exception>
    public CopiedBuildArtifacts CopyBuildArtifacts(string sourceDdbPath, string destinationDdbPath, string hash);

    /// <summary>
    /// Whether the dataset has pending (deferred) builds (DDBIsBuildPending, deprecated in libddb
    /// 2.x). Kept for the pending-build scheduling until libddb answers it through
    /// DDBBuild pendingOnly: libddb 2.x has no other dataset-wide pending query.
    /// </summary>
    public bool IsBuildPending(string ddbPath);

    /// <summary>
    /// The pending (deferred) builds of the dataset: builds skipped because a dependency was
    /// missing at the last attempt (DDBGetPendingBuildInfo, deprecated in libddb 2.x).
    /// Read-only. Kept for the same reason as <see cref="IsBuildPending"/>.
    /// </summary>
    public PendingBuildInfo[] GetPendingBuildInfo(string ddbPath);

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

    /// <summary>
    /// The data-only STAC document (<c>DDBStacData</c>): the Item of <paramref name="entry"/>, or the
    /// Collection of the dataset when it is null or empty, without any member made from a URL or an ID ("id",
    /// "collection", "links", asset "href"s), which Registry owns. Keys in libddb's (sorted) order.
    /// </summary>
    /// <exception cref="DdbNotFoundException">No entry has this path.</exception>
    public JsonElement StacData(string ddbPath, string? entry);

    /// <summary>
    /// The data-only STAC ItemCollection (<c>DDBStacItemCollectionData</c>): the entries with a geometry,
    /// in path order, filtered by <paramref name="bbox"/> ("minX,minY,maxX,maxY", EPSG:4326) and
    /// <paramref name="datetime"/> (RFC 3339 instant or interval), paged; <c>numberMatched</c> counts every match.
    /// </summary>
    public JsonElement StacItemCollectionData(string ddbPath, string? bbox, string? datetime, int limit, int offset);

    /// <summary>
    /// Rescans all files in the index to update metadata
    /// </summary>
    /// <param name="ddbPath">Path to the DroneDB database</param>
    /// <param name="options">What to rescan; null rescans every entry and stops on the first error</param>
    /// <returns>List of rescan results for each processed entry</returns>
    public List<RescanResult> RescanIndex(string ddbPath, RescanOptions? options = null);
    string ThumbnailMimeType { get; }

    /// <summary>
    /// Get raster info including bands, detected sensor, and presets, and <c>indexLayer</c>: the spectral
    /// indices the raster can render as a map layer (<see cref="Registry.Ports.DroneDB.RasterIndexLayer"/>)
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
    /// Validate that source and reference rasters are compatible for alignment
    /// </summary>
    public string ValidateAlignRaster(string sourcePath, string referencePath);

    /// <summary>
    /// Align a source GeoTIFF to a reference GeoTIFF and write the output COG
    /// </summary>
    public string AlignRaster(string sourcePath, string referencePath, string outputPath, string mode = "similarity");

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF
    /// </summary>
    public void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF using the
    /// block-windowed implementation (bounded peak memory), with incremental
    /// progress reporting and cooperative cancellation. Raises
    /// <c>DdbCanceledException</c> when canceled via the token.
    /// </summary>
    public void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale,
        int tileSize, System.Action<double, string?>? progress,
        System.Threading.CancellationToken cancellationToken);

    /// <summary>
    /// Writes the training data of the ODM project in the dataset folder <paramref name="folder"/> to
    /// <paramref name="outFolder"/> (DDBExportSplatTrainingData): a COLMAP sparse model, the images and
    /// the georeference of the project. Reports the fraction done and the phase; a canceled call raises
    /// <c>DdbCanceledException</c>.
    /// </summary>
    public SplatTrainingExport ExportSplatTrainingData(string ddbPath, string folder, string outFolder,
        SplatTrainingOptions? options, System.Action<double, string?>? progress,
        System.Threading.CancellationToken cancellationToken);

    /// <summary>
    /// Makes the <c>.spz</c> of the splat <paramref name="entryPath"/> in the build folder of the entry, or
    /// returns the file that is there (DDBExportSplatSpz). Raises <c>DdbBuildInProgressException</c> while a build
    /// or another export of the entry holds its build lock.
    /// </summary>
    public SplatSpzExport ExportSplatSpz(string ddbPath, string entryPath,
        System.Threading.CancellationToken cancellationToken);

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
    /// <param name="indexName">One of NDVI / NDRE / NDWI / SAVI / EVI (case-insensitive), as listed by the raster's
    /// <c>indexLayer</c> (<see cref="GetRasterInfo"/>): libddb refuses the others.</param>
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
    /// <param name="query">Layer, filters, paging and output format.</param>
    /// <returns>Encoded features as a string.</returns>
    public string QueryVector(string vectorPath, VectorQuery query);

    /// <summary>
    /// Number of features of <paramref name="query"/>'s layer that match its bbox and feature ids
    /// (paging and format are ignored): WFS numberMatched. A GeoPackage counts with its R-tree
    /// and primary key, without reading features.
    /// </summary>
    public long CountVector(string vectorPath, VectorQuery query);

    /// <summary>
    /// Describe a vector dataset (WFS DescribeFeatureType / OGC API collection).
    /// </summary>
    /// <param name="vectorPath">Path to the vector source.</param>
    /// <param name="layerName">Layer to describe; null = all layers.</param>
    /// <returns>JSON describing driver, layers, fields, geometryType, srs, extent and feature count.</returns>
    public string DescribeVector(string vectorPath, string? layerName = null);

    /// <summary>
    /// Content of a .prj that gives a CAD drawing the coordinate system <paramref name="crs"/>
    /// (DDBMakeCadPrj): ESRI WKT for an authority code, WKT or PROJ string; LOCAL_CS for "local".
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="crs"/> is not a coordinate system.</exception>
    public string MakeCadPrj(string crs);
}
