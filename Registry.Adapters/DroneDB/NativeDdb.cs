using System;
using System.Runtime.InteropServices;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// The libddb C ABI as Registry calls it (ddb.h of DroneDB 2.3.0, ABI 2.3): the only place an export
/// is declared. Every declaration is a source-generated
/// <see cref="LibraryImportAttribute"/> stub named exactly like its export, so
/// <see cref="DdbAbi.RequiredSymbols"/> (the startup check) is derived from this class.
/// <list type="bullet">
/// <item>Strings are UTF-8 both ways; string arrays (<c>const char **</c>) are marshalled by the
/// generator, freed when the call returns.</item>
/// <item>Every C <c>bool</c> is 1 byte: <c>[MarshalAs(UnmanagedType.U1)]</c> on each
/// <see cref="bool"/> (the generator refuses an unannotated one, SYSLIB1051).</item>
/// <item>Structs are blittable (<see cref="DdbAddOptions"/>, <see cref="DdbAddHint"/>), with their sizes
/// checked by tests.</item>
/// <item>Outputs (<c>char **</c>, <c>uint8_t **</c>) are written by libddb only on
/// <see cref="DdbResult.Success"/>; <see cref="NativeCall"/> frees them (<see cref="DDBFree"/>,
/// <see cref="DDBVSIFree"/>) in a finally block.</item>
/// <item>Callbacks are <c>[UnmanagedCallersOnly]</c> function pointers that never throw.</item>
/// </list>
/// Call these through <see cref="NativeCall"/>, which reads the last error synchronously after a
/// failed call, never directly from a managed API.
/// </summary>
internal static unsafe partial class NativeDdb
{
    private const string Library = "ddb";

    // ------------------------------------------------------------------ exempt accessors

    [LibraryImport(Library)]
    internal static partial IntPtr DDBGetLastError();

    [LibraryImport(Library)]
    internal static partial IntPtr DDBGetLastErrorReason();

    [LibraryImport(Library)]
    internal static partial IntPtr DDBGetVersion();

    /// <summary><c>(major &lt;&lt; 16) | minor</c> of the loaded library. Callable at any time.</summary>
    [LibraryImport(Library)]
    internal static partial uint DDBGetAbiVersion();

    /// <summary>The capabilities JSON, owned by the library for the life of the process (do not free).</summary>
    [LibraryImport(Library)]
    internal static partial IntPtr DDBGetCapabilities();

    [LibraryImport(Library)]
    internal static partial DdbResult DDBFree(IntPtr ptr);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBVSIFree(IntPtr buffer);

    // ------------------------------------------------------------------ process

    [LibraryImport(Library)]
    internal static partial DdbResult DDBRegisterProcess([MarshalAs(UnmanagedType.U1)] bool verbose);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBConfigure(string? configJson);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBGetEffectiveConfig(out IntPtr output);

    /// <summary>Process-final: no call but the exempt accessors works after it.</summary>
    [LibraryImport(Library)]
    internal static partial DdbResult DDBShutdown(int timeoutMs);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBCreateCancelToken(out IntPtr token);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBCancel(IntPtr token);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBFreeCancelToken(IntPtr token);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBSetThreadCancelToken(IntPtr token);

    /// <summary>
    /// <c>DDBLogCallback</c>: <c>void (*)(int level, const char *message, const char *source,
    /// uint64_t opId, void *userData)</c>, the platform's default C calling convention.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial DdbResult DDBSetLogCallback(
        delegate* unmanaged[Cdecl]<int, byte*, byte*, ulong, IntPtr, void> callback, int minLevel, IntPtr userData);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBSetThreadOperation(ulong opId);

    // ------------------------------------------------------------------ index

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBNormalizePath(string? path, string? reservedJson, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBInit(string directory, out IntPtr outPath);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBAdd(string ddbPath, string[] paths, int numPaths, out IntPtr output,
        [MarshalAs(UnmanagedType.U1)] bool recursive);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBAddWithOptions(string ddbPath, string[] paths, int numPaths,
        in DdbAddOptions options, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBAddWithHints(string ddbPath, DdbAddHint* files, int numFiles,
        in DdbAddOptions options, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBRemove(string ddbPath, string[] paths, int numPaths);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBInfo(string[] paths, int numPaths, out IntPtr output, string format,
        [MarshalAs(UnmanagedType.U1)] bool recursive, int maxRecursionDepth, string geometry,
        [MarshalAs(UnmanagedType.U1)] bool withHash, [MarshalAs(UnmanagedType.U1)] bool stopOnError);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBReleaseConnections(string path, out int closed);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBCheckpoint(string ddbPath, out int complete);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGet(string ddbPath, string path, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBListEntries(string ddbPath, string folder,
        [MarshalAs(UnmanagedType.U1)] bool recursive, string namePattern, string types,
        [MarshalAs(UnmanagedType.U1)] bool withDetails, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetStamp(string ddbPath, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetStampChecksum(string ddbPath, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBDelta(string ddbSourceStamp, string ddbTargetStamp, out IntPtr output,
        string format);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBApplyDeltaEx(string? delta, string? sourcePath, string? ddbPath,
        int mergeStrategy, string? sourceMetaDump, string? verifiedHashesJson, out IntPtr conflicts);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBComputeDeltaLocals(string delta, string ddbPath, string hlDestFolder,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMoveEntry(string ddbPath, string source, string dest);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBRescan(string ddbPath, out IntPtr output, string types,
        [MarshalAs(UnmanagedType.U1)] bool stopOnError);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBRescanEx(string ddbPath, out IntPtr output, string types, string extensions,
        [MarshalAs(UnmanagedType.U1)] bool stopOnError);

    // ------------------------------------------------------------------ build

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBBuild(string ddbPath, string? source, string? dest,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool pendingOnly);

    /// <summary>DDBBuild with a JSON object of limits (<c>{"threads": N}</c>), null or empty for none.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBBuildEx(string ddbPath, string? source, string? dest,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool pendingOnly,
        string? optionsJson);

    /// <summary>
    /// <c>DDBBuildEx</c> with a progress callback (ABI 2.3, FR-PRC-42): the fraction of the build (-1 until something is
    /// measured, never going down, 1 at the end) and its phase (the entry type, "warping", "overviews", "compressing",
    /// "publishing", null at the end); a non-zero return cancels the build.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBBuildEx2(string ddbPath, string? source, string? dest,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool pendingOnly, string? optionsJson,
        delegate* unmanaged[Cdecl]<double, byte*, IntPtr, int> progress, IntPtr progressUserData);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBCleanup(string ddbPath, out IntPtr output);

    /// <summary>Deprecated in libddb 2.x (no other dataset-wide pending query yet).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBIsBuildPending(string ddbPath,
        [MarshalAs(UnmanagedType.U1)] out bool isBuildPending);

    /// <summary>Deprecated in libddb 2.x (no other dataset-wide pending query yet).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetPendingBuildInfo(string ddbPath, out IntPtr output);

    [LibraryImport(Library)]
    internal static partial DdbResult DDBGetEntryTypes(out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetBuildArtifacts(string? ddbPath, string?[]? paths, int numPaths,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBResolveBuildFile(string? ddbPath, string? hash, string? path,
        out IntPtr outPath);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBCopyBuildArtifacts(string? sourceDdbPath, string? destinationDdbPath,
        string? hash, out IntPtr output);

    // ------------------------------------------------------------------ meta

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMetaAdd(string ddbPath, string path, string key, string data,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMetaSet(string ddbPath, string path, string key, string data,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMetaRemove(string ddbPath, string id, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMetaGet(string ddbPath, string path, string key, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMetaUnset(string ddbPath, string path, string key, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMetaList(string ddbPath, string path, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMetaDump(string ddbPath, string ids, out IntPtr output);

    // ------------------------------------------------------------------ STAC

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBStacData(string? ddbPath, string? entry, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBStacItemCollectionData(string? ddbPath, string? bbox, string? datetime,
        int limit, int offset, out IntPtr output);

    // ------------------------------------------------------------------ thumbnails and tiles

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGenerateMemoryThumbnail(string filePath, int size, out IntPtr outBuffer,
        out int outBufferSize);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGenerateMemoryThumbnailEx(string filePath, int size, string? preset,
        string? bands, string? formula, string? bandFilter, string? colormap, string? rescale,
        out IntPtr outBuffer, out int outBufferSize);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMemoryTileFmt(string inputPath, int tz, int tx, int ty, out IntPtr outBuffer,
        out int outBufferSize, int tileSize, [MarshalAs(UnmanagedType.U1)] bool tms,
        [MarshalAs(UnmanagedType.U1)] bool forceRecreate, string inputPathHash, string outputFormat);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMemoryTileEx(string inputPath, int tz, int tx, int ty, int tileSize,
        [MarshalAs(UnmanagedType.U1)] bool tms, [MarshalAs(UnmanagedType.U1)] bool forceRecreate,
        string inputPathHash, string? preset, string? bands, string? formula, string? bandFilter, string? colormap,
        string? rescale, out IntPtr outBuffer, out int outBufferSize);

    /// <summary>DDBGenerateMemoryThumbnailEx with a <c>DDBRasterOptions</c> (ABI 2.2).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGenerateMemoryThumbnailEx2(string filePath, int size, string? preset,
        string? bands, string? formula, string? bandFilter, string? colormap, string? rescale,
        NativeRasterOptions* options, out IntPtr outBuffer, out int outBufferSize);

    /// <summary>DDBMemoryTileEx with a <c>DDBRasterOptions</c> (ABI 2.2).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMemoryTileEx2(string inputPath, int tz, int tx, int ty, int tileSize,
        [MarshalAs(UnmanagedType.U1)] bool tms, [MarshalAs(UnmanagedType.U1)] bool forceRecreate,
        string inputPathHash, string? preset, string? bands, string? formula, string? bandFilter, string? colormap,
        string? rescale, NativeRasterOptions* options, out IntPtr outBuffer, out int outBufferSize);

    // ------------------------------------------------------------------ rasters

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterInfo(string path, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterMetadata(string path, string? formula, string? bandFilter,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBValidateMergeMultispectral(string[] paths, int numPaths, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBPreviewMergeMultispectral(string[] paths, int numPaths, string? previewBands,
        int thumbSize, out IntPtr outBuffer, out int outBufferSize);

    /// <summary>
    /// <c>DDBMergeMultispectralEx</c> (ABI 2.1): an output that exists is replaced only with
    /// <see cref="DdbOverwriteOptions.Overwrite"/>, in one rename once the merge is complete.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMergeMultispectralEx(string[] paths, int numPaths, string outputPath,
        in DdbOverwriteOptions options);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBValidateAlignRaster(string sourcePath, string referencePath,
        out IntPtr output);

    /// <summary>
    /// <c>DDBAlignRasterEx</c> (ABI 2.1): an output that exists is replaced only with
    /// <see cref="DdbOverwriteOptions.Overwrite"/>, in one rename once the aligned file is complete.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBAlignRasterEx(string sourcePath, string referencePath, string outputPath,
        string? mode, in DdbOverwriteOptions options, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBExportRaster(string inputPath, string outputPath, string? preset,
        string? bands, string? formula, string? bandFilter, string? colormap, string? rescale);

    /// <summary><c>DDBProgressCallback</c>: <c>int (*)(double fraction, const char *phase, void *userData)</c>.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBExportRaster2(string inputPath, string outputPath, string? preset,
        string? bands, string? formula, string? bandFilter, string? colormap, string? rescale, int tileSize,
        delegate* unmanaged[Cdecl]<double, byte*, IntPtr, int> progress, IntPtr progressUserData);

    /// <summary>DDBExportRaster2 with a <c>DDBRasterOptions</c> (ABI 2.2).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBExportRaster3(string inputPath, string outputPath, string? preset,
        string? bands, string? formula, string? bandFilter, string? colormap, string? rescale, int tileSize,
        delegate* unmanaged[Cdecl]<double, byte*, IntPtr, int> progress, IntPtr progressUserData,
        NativeRasterOptions* options);

    /// <summary>
    /// The training data of an ODM project. <paramref name="optionsJson"/> is null for the defaults, else
    /// <c>{"maxImageSize": N, "maxPoints": N}</c>; the progress callback is the one of <see cref="DDBExportRaster2"/>.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBExportSplatTrainingData(string ddbPath, string folder, string outFolder,
        string? optionsJson, delegate* unmanaged[Cdecl]<double, byte*, IntPtr, int> progress, IntPtr progressUserData,
        out IntPtr output);

    /// <summary>The <c>.spz</c> of a splat, in its build folder: <c>{"path", "role", "cached"}</c>.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBExportSplatSpz(string ddbPath, string entryPath, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterValueInfo(string filePath, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterPointValue(string filePath, int x, int y, out IntPtr output);

    // The raster value calls of ABI 2.2: map coordinates (WGS 84) or pixels, and a DDBRasterOptions (null: no option)

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterValueInfoEx(string filePath, NativeRasterOptions* options,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterPointValueEx(string filePath, double lon, double lat,
        NativeRasterOptions* options, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterAreaStatsEx(string filePath, double minLon, double minLat,
        double maxLon, double maxLat, NativeRasterOptions* options, out IntPtr output);

    /// <summary>The value at a pixel (an R-JPEG's raw thermal grid) with the NoData value and the thermal formula of the options.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterPointValuePixelEx(string filePath, int x, int y,
        NativeRasterOptions* options, out IntPtr output);

    /// <summary>The statistics of a pixel rectangle (inclusive) with the NoData value and the thermal formula of the options.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterAreaStatsPixelEx(string filePath, int x0, int y0, int x1, int y1,
        NativeRasterOptions* options, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterProfileEx(string filePath, string geoJsonLineString, int samples,
        NativeRasterOptions* options, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGenerateContoursEx(string rasterPath, double interval, int count,
        double baseOffset, double minElev, double maxElev, double simplifyTolerance, int bandIndex,
        NativeRasterOptions* options, out IntPtr output);

    /// <summary><c>DDBGenerateContoursEx</c> with a progress callback (ABI 2.3).</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGenerateContoursEx2(string rasterPath, double interval, int count,
        double baseOffset, double minElev, double maxElev, double simplifyTolerance, int bandIndex,
        NativeRasterOptions* options, delegate* unmanaged[Cdecl]<double, byte*, IntPtr, int> progress,
        IntPtr progressUserData, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterAreaStats(string filePath, int x0, int y0, int x1, int y1,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGetRasterProfile(string filePath, string geoJsonLineString, int samples,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBCalculateVolume(string rasterPath, string polygonGeoJson, string baseMethod,
        double flatElevation, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBDetectStockpile(string rasterPath, double lat, double lon, double radius,
        float sensitivity, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBDetectAllStockpiles(string rasterPath, float sensitivity, double minAreaM2,
        int maxResults, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBGenerateContours(string rasterPath, double interval, int count,
        double baseOffset, double minElev, double maxElev, double simplifyTolerance, int bandIndex, out IntPtr output);

    /// <summary>
    /// <c>DDBMaskBordersEx</c> (ABI 2.1): an output that exists is replaced only with
    /// <see cref="DdbOverwriteOptions.Overwrite"/>, in one rename once the mask is complete.
    /// </summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMaskBordersEx(string input, string output, int nearDist,
        [MarshalAs(UnmanagedType.U1)] bool white, in DdbOverwriteOptions options);

    // ------------------------------------------------------------------ OGC

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBRenderRasterRegionEx(string inputPath, double[] bbox, string bboxSrs,
        string outputCrs, int[] bands, int bandCount, int width, int height, string format, out IntPtr outBuffer,
        out int outBufferSize);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBRenderRasterIndex(string inputPath, string indexName, double[] bbox,
        string bboxSrs, int width, int height, string format, out IntPtr outBuffer, out int outBufferSize);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBQueryRasterPoint(string inputPath, double x, double y, string srs,
        out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBQueryVectorEx(string vectorPath, string? layerName, double[]? bbox,
        string? bboxSrs, long[]? fids, int numFids, string? where, int maxFeatures, int startIndex,
        string outputFormat, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBCountVector(string vectorPath, string? layerName, double[]? bbox,
        string? bboxSrs, long[]? fids, int numFids, string? where, out long count);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBDescribeVector(string vectorPath, string? layerName, out IntPtr output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial DdbResult DDBMakeCadPrj(string crs, out IntPtr output);
}

/// <summary>
/// <c>DDBAddOptions</c>: 8 bytes, <c>recursive</c> at 0 and <c>stopOnError</c> at 1 (1-byte bools),
/// <c>maxConflictRetries</c> at 4. Blittable: the bools are bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DdbAddOptions
{
    internal byte Recursive;
    internal byte StopOnError;
    internal int MaxConflictRetries;

    internal static DdbAddOptions Create(bool recursive, bool stopOnError, int maxConflictRetries) => new()
    {
        Recursive = recursive ? (byte)1 : (byte)0,
        StopOnError = stopOnError ? (byte)1 : (byte)0,
        MaxConflictRetries = maxConflictRetries
    };
}

/// <summary>
/// <c>DDBAlignRasterOptions</c>, <c>DDBMergeMultispectralOptions</c> and <c>DDBMaskBordersOptions</c> (ABI 2.1, one
/// layout): 8 bytes, <c>structSize</c> at 0, <c>overwrite</c> at 4 (a 1-byte bool), three reserved bytes that must be
/// 0 at 5. Blittable: the bool is a byte.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DdbOverwriteOptions
{
    internal uint StructSize;
    internal byte Overwrite;
    internal byte Reserved0;
    internal byte Reserved1;
    internal byte Reserved2;

    internal static DdbOverwriteOptions Create(bool overwrite) => new()
    {
        StructSize = 8,
        Overwrite = overwrite ? (byte)1 : (byte)0
    };
}

/// <summary>
/// <c>DDBAddHint</c> (64-bit layout): 32 bytes, <c>path</c> at 0, <c>sha256</c> at 8 (UTF-8
/// pointers owned by the caller), <c>size</c> at 16, <c>mtime</c> at 24.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DdbAddHint
{
    internal IntPtr Path;
    internal IntPtr Sha256;
    internal long Size;
    internal double Mtime;
}

/// <summary>
/// <c>DDBRasterOptions</c> (64-bit layout): 64 bytes, <c>structSize</c> at 0, <c>flags</c> at 4, <c>nodata</c> at 8,
/// <c>bbox</c> (4 doubles) at 16, <c>formula</c> at 48 and <c>rescaleUnit</c> (ABI 2.3) at 56 (UTF-8 pointers owned by
/// the caller). Blittable. A libddb 2.2 knows 56 bytes and takes the larger struct while <c>rescaleUnit</c> is null (its
/// bytes are zero); it refuses one that sets it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeRasterOptions
{
    internal const uint FlagNodata = 0x1;
    internal const uint FlagBbox = 0x2;

    internal uint StructSize;
    internal uint Flags;
    internal double Nodata;
    internal fixed double Bbox[4];
    internal IntPtr Formula;
    internal IntPtr RescaleUnit;
}
