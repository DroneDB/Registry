#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Default <see cref="IRasterAnalysisService"/>. The arguments are checked once, here, before the access
/// check; the source is resolved by <see cref="RenderSources"/> (404 before any native call when the
/// artifact does not exist yet); every native call runs on the heavy lane of <see cref="INativeWorkGate"/>.
/// </summary>
public sealed class RasterAnalysisService : IRasterAnalysisService
{
    /// <summary>The most stockpile footprints one full scan returns.</summary>
    public const int MaxStockpileResults = 500;

    private readonly RenderSources _sources;
    private readonly INativeWorkGate _gate;
    private readonly IScratchFileProvider _scratch;
    private readonly IFileSystem _fs;
    private readonly DownloadOptions _downloads;
    private readonly ILogger<RasterAnalysisService> _logger;

    public RasterAnalysisService(RenderSources sources, INativeWorkGate gate, IScratchFileProvider scratch,
        IFileSystem fs, IOptions<DownloadOptions> downloads, ILogger<RasterAnalysisService> logger)
    {
        _sources = sources;
        _gate = gate;
        _scratch = scratch;
        _fs = fs;
        _downloads = downloads.Value;
        _logger = logger;
    }

    public async Task<string> GetRasterInfoAsync(string orgSlug, string dsSlug, string path,
        CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterInfo(source.SourcePath), ct);
    }

    public async Task<string> GetRasterMetadataAsync(string orgSlug, string dsSlug, string path, string? formula = null,
        string? bandFilter = null, CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterMetadata(source.SourcePath, formula, bandFilter), ct);
    }

    public async Task<string> GetRasterValueInfoAsync(string orgSlug, string dsSlug, string path,
        CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterValueInfo(source.SourcePath), ct);
    }

    public async Task<string> GetRasterPointValueAsync(string orgSlug, string dsSlug, string path, int x, int y,
        CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterPointValue(source.SourcePath, x, y), ct);
    }

    public async Task<string> GetRasterAreaStatsAsync(string orgSlug, string dsSlug, string path, int x0, int y0,
        int x1, int y1, CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterAreaStats(source.SourcePath, x0, y0, x1, y1), ct);
    }

    public async Task<string> GetRasterProfileAsync(string orgSlug, string dsSlug, string path,
        string geoJsonLineString, int samples, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(geoJsonLineString))
            throw new BadRequestException("lineString (GeoJSON LineString) is required");

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GetRasterProfile(source.SourcePath, geoJsonLineString, samples), ct);
    }

    public async Task<string> CalculateVolumeAsync(string orgSlug, string dsSlug, string path, string polygonGeoJson,
        string? baseMethod, double flatElevation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(polygonGeoJson))
            throw new BadRequestException("polygon (GeoJSON Polygon) is required");

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.CalculateVolume(source.SourcePath, polygonGeoJson, baseMethod ?? string.Empty,
            flatElevation), ct);
    }

    public async Task<string> DetectStockpileAsync(string orgSlug, string dsSlug, string path, double lat, double lon,
        double radiusMeters, float sensitivity, CancellationToken ct = default)
    {
        if (!(radiusMeters > 0))
            throw new BadRequestException("radius must be positive");

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.DetectStockpile(source.SourcePath, lat, lon, radiusMeters,
            Math.Clamp(sensitivity, 0f, 1f)), ct);
    }

    public async Task<string> DetectAllStockpilesAsync(string orgSlug, string dsSlug, string path, float sensitivity,
        double minAreaM2, int maxResults, CancellationToken ct = default)
    {
        if (minAreaM2 < 0) throw new BadRequestException("minAreaM2 must be >= 0");
        if (maxResults <= 0) throw new BadRequestException("maxResults must be > 0");

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.DetectAllStockpiles(source.SourcePath, Math.Clamp(sensitivity, 0f, 1f),
            minAreaM2, Math.Min(maxResults, MaxStockpileResults)), ct);
    }

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

        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);
        return await RunAsync(() => source.Ddb.GenerateContours(source.SourcePath, o.Interval, o.Count, o.BaseOffset,
            o.MinElev, o.MaxElev, o.SimplifyTolerance, o.BandIndex > 0 ? o.BandIndex : 1), ct);
    }

    public async Task<StorageDataDto> ExportRasterAsync(string orgSlug, string dsSlug, string path,
        RasterVisualization visualization, CancellationToken ct = default)
    {
        var source = await _sources.ResolveAsync(orgSlug, dsSlug, path, ct);

        // Enforce the export size limit (a conservative upper bound from the raw input size)
        await EnsureExportSizeWithinLimitAsync(source, ct);

        var stem = Path.GetFileNameWithoutExtension(source.Path);
        // The configured temp folder; deleted when the bytes are read
        using var output = _scratch.Create(stem + "_export_", ".tif");

        var v = visualization;
        await _gate.RunAsync(NativeLane.Heavy, () => source.Ddb.ExportRaster(source.SourcePath, output.Path, v.Preset,
            v.Bands, v.Formula, v.BandFilter, v.Colormap, v.Rescale), ct);

        return new StorageDataDto
        {
            Name = stem + "_export.tif",
            Data = await _fs.ReadAllBytesAsync(output.Path, ct),
            ContentType = "image/tiff"
        };
    }

    public async Task<long> EstimateExportSizeAsync(string orgSlug, string dsSlug, string path,
        CancellationToken ct = default)
    {
        return await EstimateExportBytesAsync(await _sources.ResolveAsync(orgSlug, dsSlug, path, ct), ct);
    }

    // Native raster work runs on the heavy lane of the gate (XI-06), as thumbnails do
    private Task<T> RunAsync<T>(Func<T> work, CancellationToken ct) => _gate.RunAsync(NativeLane.Heavy, work, ct);

    /// <summary>
    /// Throws <see cref="BadRequestException"/> when the estimated export exceeds
    /// <see cref="DownloadOptions.MaxExportSizeBytes"/>; nothing when there is no limit or no estimate.
    /// </summary>
    private async Task EnsureExportSizeWithinLimitAsync(RenderSource source, CancellationToken ct)
    {
        if (_downloads.MaxExportSizeBytes is not { } limit) return;

        long estimated;
        try
        {
            estimated = await EstimateExportBytesAsync(source, ct);
        }
        catch (Exception ex)
        {
            // An estimate that fails does not block the export
            _logger.LogWarning(ex, "Failed to estimate export size for '{Path}', skipping limit check", source.SourcePath);
            return;
        }

        if (estimated > limit)
            throw new BadRequestException(
                $"Export output exceeds maximum allowed size ({CommonUtils.GetBytesReadable(estimated)} > {CommonUtils.GetBytesReadable(limit)}).");
    }

    /// <summary>
    /// The raw uncompressed size (in bytes) of a raster: a conservative upper bound for its GeoTIFF export
    /// (width × height × bytes per pixel × bands).
    /// </summary>
    private async Task<long> EstimateExportBytesAsync(RenderSource source, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await RunAsync(() => source.Ddb.GetRasterInfo(source.SourcePath), ct));
        var root = doc.RootElement;

        var width = root.TryGetProperty("width", out var wEl) ? wEl.GetInt64() : 0;
        var height = root.TryGetProperty("height", out var hEl) ? hEl.GetInt64() : 0;
        var bandCount = root.TryGetProperty("bandCount", out var bEl) ? bEl.GetInt64() : 1;
        var dataType = root.TryGetProperty("dataType", out var dtEl) ? dtEl.GetString() : null;

        if (width <= 0 || height <= 0 || bandCount <= 0) return 0;

        return width * height * bandCount * CommonUtils.BytesPerPixel(dataType);
    }
}
