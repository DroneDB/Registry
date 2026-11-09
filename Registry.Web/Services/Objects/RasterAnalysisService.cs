using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Measurements on a raster entry (read from its build artifact once built): its description and statistics,
/// values at points, along lines and over areas, stockpile volumes and footprints, contour lines, and a
/// GeoTIFF export. Results are libddb's JSON, passed through.
/// </summary>
public sealed class RasterAnalysisService
{
    /// <summary>The most stockpile footprints one full scan returns.</summary>
    public const int MaxStockpileResults = 500;

    private readonly RenderSources _sources;
    private readonly INativeWorkGate _gate;
    private readonly ScratchFileProvider _scratch;
    private readonly IFileSystem _fs;
    private readonly DownloadOptions _downloads;
    private readonly ILogger<RasterAnalysisService> _logger;

    public RasterAnalysisService(RenderSources sources, INativeWorkGate gate, ScratchFileProvider scratch,
        IFileSystem fs, IOptions<DownloadOptions> downloads, ILogger<RasterAnalysisService> logger)
    {
        _sources = sources;
        _gate = gate;
        _scratch = scratch;
        _fs = fs;
        _downloads = downloads.Value;
        _logger = logger;
    }

    /// <summary>Bands, sensor profile and visualization presets.</summary>
    public async Task<string> GetRasterInfoAsync(string orgSlug, string dsSlug, string path,
        CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterInfo(source.SourcePath), ct);
    }

    /// <summary>Statistics and histogram of a band or of a band formula.</summary>
    public async Task<string> GetRasterMetadataAsync(string orgSlug, string dsSlug, string path, string? formula = null,
        string? bandFilter = null, CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterMetadata(source.SourcePath, formula, bandFilter), ct);
    }

    /// <summary>
    /// The value info of the raster file: its exact range in the units of the band, unit and dimensions (with the thermal
    /// calibration when there is one), the NoData value that applies, the default colour stretch (D-041) and the size of an
    /// export of the file or of the window <see cref="RasterAnalysisOptions.Bbox"/> (<c>estimatedExportBytes</c>) with its
    /// limit (<c>exportLimitBytes</c>, null when there is none): the Hub computes none of them (D-023).
    /// </summary>
    public async Task<string> GetRasterValueInfoAsync(string orgSlug, string dsSlug, string path,
        RasterAnalysisOptions? options = null, CancellationToken ct = default)
    {
        var json = await OnSourceAsync(orgSlug, dsSlug, path, (ddb, file) => ddb.GetRasterValueInfo(file, options), ct);
        return AddExportEstimate(json);
    }

    /// <summary>
    /// The size of an export of the raster (D-023), the one estimate of the value info and of the check of an export: the
    /// value info of the same source file (<see cref="OnSourceAsync{T}"/>, with the same fallback to the COG), with the NoData
    /// value and the window of <paramref name="options"/>, through <see cref="ExportEstimate.Bytes(JsonElement)"/>. The
    /// formula is not sent: it changes the stretch of the value info, not its pixel counts. Null when libddb counted no
    /// pixels.
    /// </summary>
    public async Task<long?> EstimateExportBytesAsync(string orgSlug, string dsSlug, string path,
        RasterAnalysisOptions? options, CancellationToken ct = default)
    {
        var counting = ExportEstimate.CountingOptions(options);
        var json = await OnSourceAsync(orgSlug, dsSlug, path, (ddb, file) => ddb.GetRasterValueInfo(file, counting), ct);
        using var doc = JsonDocument.Parse(json);
        return ExportEstimate.Bytes(doc.RootElement);
    }

    /// <summary>
    /// The value (temperature, elevation, ...) at the pixel <paramref name="x"/>, <paramref name="y"/> of an R-JPEG's raw
    /// thermal grid, with the options of the values. A georeferenced raster takes map coordinates (§7.1): pixel
    /// coordinates on it are a 400 (<c>errors.x</c>).
    /// </summary>
    public async Task<string> GetRasterPointValueAsync(string orgSlug, string dsSlug, string path, int x, int y,
        RasterAnalysisOptions? options = null, CancellationToken ct = default)
    {
        return await OnSourceAsync(orgSlug, dsSlug, path, (ddb, file) => ddb.GetRasterPointValue(file, x, y, options), ct,
            entry => RequirePixelCoordinates(entry, "x", "lon, lat"));
    }

    /// <summary>
    /// The value at a WGS 84 point: the pixel of the file under it, found in libddb with its one geo-to-pixel conversion
    /// (D-021).
    /// </summary>
    public async Task<string> GetRasterPointValueAsync(string orgSlug, string dsSlug, string path, double lon,
        double lat, RasterAnalysisOptions? options = null, CancellationToken ct = default)
    {
        if (!double.IsFinite(lon) || !double.IsFinite(lat))
            throw new BadRequestException("'lon' and 'lat' must be finite numbers");

        return await OnSourceAsync(orgSlug, dsSlug, path, (ddb, file) => ddb.GetRasterPointValue(file, lon, lat, options),
            ct);
    }

    /// <summary>
    /// Value statistics over a pixel rectangle of an R-JPEG's raw thermal grid, with the options of the values. A
    /// georeferenced raster takes a WGS 84 window (§7.1): pixel coordinates on it are a 400 (<c>errors.x0</c>).
    /// </summary>
    public async Task<string> GetRasterAreaStatsAsync(string orgSlug, string dsSlug, string path, int x0, int y0,
        int x1, int y1, RasterAnalysisOptions? options = null, CancellationToken ct = default)
    {
        return await OnSourceAsync(orgSlug, dsSlug, path,
            (ddb, file) => ddb.GetRasterAreaStats(file, x0, y0, x1, y1, options), ct,
            entry => RequirePixelCoordinates(entry, "x0", "bbox"));
    }

    /// <summary>Value statistics over a WGS 84 rectangle <c>[minLon, minLat, maxLon, maxLat]</c>: the pixels of the file under it.</summary>
    public async Task<string> GetRasterAreaStatsAsync(string orgSlug, string dsSlug, string path, double[] bbox,
        RasterAnalysisOptions? options = null, CancellationToken ct = default)
    {
        RasterOptionRules.RequireWindow(bbox);
        return await OnSourceAsync(orgSlug, dsSlug, path, (ddb, file) =>
            ddb.GetRasterAreaStats(file, bbox[0], bbox[1], bbox[2], bbox[3], options), ct);
    }

    /// <summary>Values sampled along a GeoJSON LineString (WGS84).</summary>
    public async Task<string> GetRasterProfileAsync(string orgSlug, string dsSlug, string path,
        string geoJsonLineString, int samples, RasterAnalysisOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(geoJsonLineString))
            throw new BadRequestException("lineString (GeoJSON LineString) is required");

        return await OnSourceAsync(orgSlug, dsSlug, path, (ddb, file) =>
            ddb.GetRasterProfile(file, geoJsonLineString, samples, options), ct);
    }

    /// <summary>Cut, fill and net volume over a GeoJSON polygon of an elevation raster.</summary>
    public async Task<string> CalculateVolumeAsync(string orgSlug, string dsSlug, string path, string polygonGeoJson,
        string? baseMethod, double flatElevation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(polygonGeoJson))
            throw new BadRequestException("polygon (GeoJSON Polygon) is required");

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.CalculateVolume(source.SourcePath, polygonGeoJson, baseMethod ?? string.Empty,
            flatElevation), ct);
    }

    /// <summary>The footprint of the stockpile around a clicked location.</summary>
    public async Task<string> DetectStockpileAsync(string orgSlug, string dsSlug, string path, double lat, double lon,
        double radiusMeters, float sensitivity, CancellationToken ct = default)
    {
        if (!(radiusMeters > 0))
            throw new BadRequestException("radius must be positive");

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.DetectStockpile(source.SourcePath, lat, lon, radiusMeters,
            Math.Clamp(sensitivity, 0f, 1f)), ct);
    }

    /// <summary>The footprints of every stockpile of the elevation raster (a full scan).</summary>
    public async Task<string> DetectAllStockpilesAsync(string orgSlug, string dsSlug, string path, float sensitivity,
        double minAreaM2, int maxResults, CancellationToken ct = default)
    {
        if (minAreaM2 < 0) throw new BadRequestException("minAreaM2 must be >= 0");
        if (maxResults <= 0) throw new BadRequestException("maxResults must be > 0");

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.DetectAllStockpiles(source.SourcePath, Math.Clamp(sensitivity, 0f, 1f),
            minAreaM2, Math.Min(maxResults, MaxStockpileResults)), ct);
    }

    /// <summary>Contour lines of a single-band elevation raster (DEM/DSM/DTM).</summary>
    public async Task<string> GenerateContoursAsync(string orgSlug, string dsSlug, string path, ContourOptions options,
        CancellationToken ct = default)
    {
        var o = options;
        if (!o.Interval.HasValue && !o.Count.HasValue)
            throw new BadRequestException("Either 'interval' or 'count' must be specified");
        if (o.Interval <= 0)
            throw new BadRequestException("'interval' must be > 0");
        if (o.Count <= 0)
            throw new BadRequestException("'count' must be > 0");
        if (o.MinElev >= o.MaxElev)
            throw new BadRequestException("'minElev' must be less than 'maxElev'");
        if (o.SimplifyTolerance < 0)
            throw new BadRequestException("'simplifyTolerance' must be >= 0");

        RasterOptionRules.RequireNodata(o.Nodata);
        return await OnSourceAsync(orgSlug, dsSlug, path, (ddb, file) => ddb.GenerateContours(file, o.Interval, o.Count,
            o.BaseOffset, o.MinElev, o.MaxElev, o.SimplifyTolerance, o.BandIndex > 0 ? o.BandIndex : 1,
            o.Nodata is null ? null : new RasterAnalysisOptions(o.Nodata)), ct);
    }

    /// <summary>The raster as a GeoTIFF with the visualization applied, within the export size limit.</summary>
    public async Task<StorageDataDto> ExportRasterAsync(string orgSlug, string dsSlug, string path,
        RasterVisualization visualization, CancellationToken ct = default)
    {
        RasterOptionRules.RequireNodata(visualization.Nodata);
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);

        // Enforce the export size limit with the estimate of the value info (the pixels of the source file that are not
        // NoData x 4 bytes, D-023), so what the Hub shows as within the limit is what the export accepts
        await EnsureExportSizeWithinLimitAsync(orgSlug, dsSlug, path, new RasterAnalysisOptions(visualization.Nodata), ct);

        var stem = Path.GetFileNameWithoutExtension(source.Path);
        // The configured temp folder; deleted when the bytes are read
        using var output = _scratch.Create(stem + "_export_", ".tif");

        var v = visualization;
        await _gate.RunAsync(NativeLane.Heavy, () =>
        {
            if (v.Nodata is null)
                source.Ddb.ExportRaster(source.SourcePath, output.Path, v.Preset, v.Bands, v.Formula, v.BandFilter,
                    v.Colormap, v.Rescale);
            else
                source.Ddb.ExportRaster(source.SourcePath, output.Path, v.Preset, v.Bands, v.Formula, v.BandFilter,
                    v.Colormap, v.Rescale, 0, v.Nodata, null, ct);
        }, ct);

        return new StorageDataDto
        {
            Name = stem + "_export.tif",
            Data = await _fs.ReadAllBytesAsync(output.Path, ct),
            ContentType = "image/tiff"
        };
    }

    /// <summary>
    /// The most pixels (width x height) of a source file libddb reads in one piece (its <c>kMaxNormalizePixels</c>): the
    /// values of a larger one are read from the COG of its build.
    /// </summary>
    public const long MaxSourcePixels = 1_000_000_000;

    /// <summary>
    /// Runs a read of raster values on the source file of the entry (D-021), on the heavy lane. A source that libddb cannot
    /// read in one piece (more than <see cref="MaxSourcePixels"/>, from the width and height the index holds for the entry)
    /// is read from the COG of its build instead, which is the one raster that is always readable; its values are then
    /// those of the resampled COG. <paramref name="check"/> refuses a request the entry cannot answer before any read.
    /// </summary>
    private async Task<T> OnSourceAsync<T>(string orgSlug, string dsSlug, string path,
        Func<IDDB, string, T> read, CancellationToken ct, Action<Entry>? check = null)
    {
        var source = await _sources.ResolveSourceFileAsync(orgSlug, dsSlug, path, ct);
        check?.Invoke(source.Entry);

        var pixels = PixelsOf(source.Entry);
        if (pixels > MaxSourcePixels)
        {
            _logger.LogInformation("'{Path}' has {Pixels} pixels, too many to read in one piece: reading the COG of its build",
                source.Path, pixels);
            return await OnBuildAsync(orgSlug, dsSlug, path, read, ct);
        }

        try
        {
            return await RunAsync(() => read(source.Ddb, source.SourcePath), ct);
        }
        catch (InvalidOperationException ex) when (pixels is null && TooLargeToRead(ex))
        {
            // An entry indexed without its size: libddb's refusal tells it instead
            _logger.LogWarning("'{Path}' is too large to read in one piece: reading the COG of its build", source.Path);
            return await OnBuildAsync(orgSlug, dsSlug, path, read, ct);
        }
    }

    private async Task<T> OnBuildAsync<T>(string orgSlug, string dsSlug, string path, Func<IDDB, string, T> read,
        CancellationToken ct)
    {
        var built = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => read(built.Ddb, built.SourcePath), ct);
    }

    /// <summary>The width x height the index holds for the entry (libddb's probe of the file); null when it has none.</summary>
    private static long? PixelsOf(Entry entry)
    {
        if (entry.Properties is not { } properties) return null;
        if (!properties.TryGetValue("width", out var w) || w.ValueKind != JsonValueKind.Number ||
            !w.TryGetInt64(out var width) || width <= 0)
            return null;
        if (!properties.TryGetValue("height", out var h) || h.ValueKind != JsonValueKind.Number ||
            !h.TryGetInt64(out var height) || height <= 0)
            return null;
        return width * height;
    }

    // Pixel coordinates are those of an R-JPEG's raw thermal grid, which has no geotransform (§7.1, FR-RAS-15); a
    // georeferenced raster is read at map coordinates, by libddb's one geo-to-pixel conversion (D-021)
    private static void RequirePixelCoordinates(Entry entry, string field, string mapParameters)
    {
        if (entry.Type == EntryType.GeoRaster)
            throw new InvalidFieldException(field,
                $"Pixel coordinates are for R-JPEG images only: '{entry.Path}' is a georeferenced raster, send {mapParameters} (WGS 84)");
    }

    // libddb's refusal of a raster of more pixels than a copy can hold names the way out: "build the dataset first"
    private static bool TooLargeToRead(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            if (e.Message.Contains("build the dataset first", StringComparison.OrdinalIgnoreCase)) return true;
            if (e.InnerException == null) break;
        }

        return false;
    }

    // Native raster work runs on the heavy lane of the gate, as thumbnails do
    private Task<T> RunAsync<T>(Func<T> work, CancellationToken ct) => _gate.RunAsync(NativeLane.Heavy, work, ct);

    /// <summary>
    /// Throws <see cref="BadRequestException"/> when the estimated export (<see cref="EstimateExportBytesAsync"/>) exceeds
    /// <see cref="DownloadOptions.MaxExportSizeBytes"/>; nothing when there is no limit or no estimate. The one check of
    /// an export: the direct export and the submit of the raster-export task (<see cref="IRasterExportSizeCheck"/>).
    /// </summary>
    public async Task EnsureExportSizeWithinLimitAsync(string orgSlug, string dsSlug, string path,
        RasterAnalysisOptions options, CancellationToken ct = default)
    {
        if (_downloads.MaxExportSizeBytes is not { } limit) return;

        long? estimated;
        try
        {
            // The estimate of the value info, on the same file with the same NoData value and window
            estimated = await EstimateExportBytesAsync(orgSlug, dsSlug, path, options, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An estimate that fails does not block the export
            _logger.LogWarning(ex, "Failed to estimate export size for '{Path}', skipping limit check", path);
            return;
        }

        if (estimated is { } bytes && bytes > limit)
            throw new BadRequestException(
                $"Export output exceeds maximum allowed size ({CommonUtils.GetBytesReadable(bytes)} > {CommonUtils.GetBytesReadable(limit)}).");
    }

    /// <summary>
    /// The value info of libddb with the size of its export and the limit (D-023): <c>estimatedExportBytes</c> (absent when
    /// libddb counted no pixels) and <c>exportLimitBytes</c> (null when there is no limit).
    /// </summary>
    private string AddExportEstimate(string json)
    {
        var info = JsonNode.Parse(json)?.AsObject();
        if (info is null) return json;

        using var doc = JsonDocument.Parse(json);
        if (ExportEstimate.Bytes(doc.RootElement) is { } bytes)
            info["estimatedExportBytes"] = bytes;
        info["exportLimitBytes"] = _downloads.MaxExportSizeBytes;
        return info.ToJsonString();
    }
}

/// <summary>
/// The size of the export of a styled raster (D-023), one function for the value info and for the check of an export: the
/// number of the pixels that are not NoData x 4 bytes, with no compression factor. For a window, the source pixels in
/// the window x the valid fraction of the whole file.
/// </summary>
public static class ExportEstimate
{
    /// <summary>Bytes of one exported pixel (RGBA, or a Float32 value).</summary>
    public const long BytesPerPixel = 4;

    /// <summary>
    /// The options of the value info that the estimate depends on: the NoData value and the window. The formula changes the
    /// stretch of the value info, not its pixel counts, so the estimate of a value info with a formula is the same.
    /// </summary>
    public static RasterAnalysisOptions? CountingOptions(RasterAnalysisOptions? options) =>
        options is null || (options.Nodata is null && options.Bbox is null)
            ? null
            : new RasterAnalysisOptions(options.Nodata, null, options.Bbox);

    /// <summary>
    /// The estimate from the value info of libddb (<c>validPixelCount</c>, <c>pixelCount</c> and, for a window,
    /// <c>windowPixelCount</c>); null when it has none of them.
    /// </summary>
    public static long? Bytes(JsonElement info)
    {
        if (!info.TryGetProperty("validPixelCount", out var validElement) || !validElement.TryGetInt64(out var valid))
            return null;

        if (!info.TryGetProperty("windowPixelCount", out var windowElement) || !windowElement.TryGetInt64(out var window))
            return Bytes(valid, null, null);

        long? total = info.TryGetProperty("pixelCount", out var totalElement) && totalElement.TryGetInt64(out var t) ? t : null;
        return Bytes(valid, total, window);
    }

    /// <summary>The estimate of <paramref name="validPixels"/> pixels, or of the part of them in a window of <paramref name="windowPixels"/> source pixels.</summary>
    public static long Bytes(long validPixels, long? totalPixels, long? windowPixels)
    {
        if (windowPixels is not { } window)
            return validPixels * BytesPerPixel;
        if (totalPixels is not > 0)
            return window * BytesPerPixel;

        // The valid fraction of the whole file, applied to the window
        var fraction = (double)validPixels / totalPixels.Value;
        return (long)Math.Round(window * fraction) * BytesPerPixel;
    }
}
