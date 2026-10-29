using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Registry.Ports.Json;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>
/// Shared domain operations used by every <see cref="IWcsProtocolHandler"/>.
/// Inherits <see cref="OgcManagerBase"/> only to reuse <c>ResolveAsync</c>,
/// <c>ResolveRasterArtifact</c>, <c>GetServiceUrl</c> and the cache; nothing
/// version-specific lives here.
/// </summary>
public class WcsCoverageService : OgcManagerBase
{
    private readonly ILogger<WcsCoverageService> _logger;
    private readonly INativeWorkGate _gate;
    private readonly RasterInfoCache _rasterInfo;

    public WcsCoverageService(OgcManagerDependencies dependencies, RasterInfoCache rasterInfo,
        ILogger<WcsCoverageService> logger, INativeWorkGate? gate = null)
        : base(dependencies)
    {
        _logger = logger;
        _rasterInfo = rasterInfo;
        _gate = gate ?? Services.Adapters.NativeWorkGate.Default;
    }

    /// <summary>List all raster coverages exposed by the dataset (already filtered to
    /// <see cref="Registry.Ports.DroneDB.EntryType.GeoRaster"/> with a valid WGS84 bbox).</summary>
    public async Task<(IDDB Ddb, IReadOnlyList<OgcLayerDto> Layers)> GetCoveragesAsync(
        string orgSlug, string dsSlug, string? folderPath, CancellationToken ct = default)
    {
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var layers = (await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath, ct: ct))
            .Where(l => l.EntryType == EntryType.GeoRaster && l.HasBuiltArtifact && l.BboxWgs84 != null)
            .ToList();
        return (ddb, layers);
    }

    /// <summary>Resolve a single coverage by id. Throws <see cref="Registry.Web.Exceptions.OgcException"/>
    /// with code <c>NoSuchCoverage</c> when the id is unknown.</summary>
    public async Task<(IDDB Ddb, OgcLayerDto Layer)> ResolveCoverageAsync(
        string orgSlug, string dsSlug, string coverageId, CancellationToken ct = default)
    {
        var name = Uri.UnescapeDataString(coverageId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, ct: ct)
                    ?? throw new OgcException("NoSuchCoverage",
                        $"Coverage '{coverageId}' not found", 404);
        if (layer.EntryType != EntryType.GeoRaster)
            throw new OgcException("InvalidParameterValue",
                "WCS supports only raster coverages", 400);
        RequireBuiltRaster(layer, "CoverageId");
        return (ddb, layer);
    }

    /// <summary>Resolve multiple coverages at once. Missing ids are reported in one go.</summary>
    public async Task<(IDDB Ddb, IReadOnlyList<(string Id, OgcLayerDto Layer)> Coverages)> ResolveCoveragesAsync(
        string orgSlug, string dsSlug, IEnumerable<string> coverageIds, CancellationToken ct = default)
    {
        var ids = coverageIds
            .Select(s => Uri.UnescapeDataString(s ?? string.Empty).Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (ids.Length == 0)
            throw new OgcException("MissingParameterValue", "CoverageId is required", 400, "CoverageId");

        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var resolved = new List<(string Id, OgcLayerDto Layer)>(ids.Length);
        var missing = new List<string>();
        foreach (var id in ids)
        {
            var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, id, ct: ct);
            if (layer == null) missing.Add(id);
            else
            {
                RequireBuiltRaster(layer, "CoverageId");
                resolved.Add((id, layer));
            }
        }

        if (missing.Count > 0)
            throw new OgcException("NoSuchCoverage",
                $"Coverage{(missing.Count > 1 ? "s" : "")} '{string.Join(",", missing)}' not found",
                404, "CoverageId");
        return (ddb, resolved);
    }

    /// <summary>Probe raster width/height/band metadata for a coverage, cached per raster content
    /// (entry hash). Failures are caught and reported as zeros so callers can fall back to
    /// defaults (and are not cached).</summary>
    public async Task<WcsRasterInfo> ProbeRasterAsync(IDDB ddb, OgcLayerDto layer, CancellationToken ct = default)
    {
        try
        {
            // The build COG and the source it falls back to are probed separately; the read is the one WMS shares
            var info = await _rasterInfo.GetAsync(layer.EntryHash, ResolveRasterSource(ddb, layer), ct);
            return ParseRasterInfo(info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WCS: probing raster info failed");
            return WcsRasterInfo.Empty;
        }
    }

    private static WcsRasterInfo ParseRasterInfo(string info)
    {
        if (string.IsNullOrEmpty(info)) return WcsRasterInfo.Empty;
        using var document = JsonDocument.Parse(info);
        var j = document.RootElement;
        var width = Int(j, "width");
        var height = Int(j, "height");
        var bandCount = Int(j, "bandCount");
        var nativeCrs = j.Member("crs").Text() ?? string.Empty;
        var bandNames = new List<string>();

        if (j.Member("bands") is not { ValueKind: JsonValueKind.Array } ba)
            return new WcsRasterInfo(width, height, bandCount, bandNames, nativeCrs);

        foreach (var b in ba.Items())
            bandNames.Add(b.Member("name").Text() ?? $"band{bandNames.Count + 1}");
        return new WcsRasterInfo(width, height, bandCount, bandNames, nativeCrs);
    }

    private static int Int(JsonElement parent, string name) =>
        parent.Member(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>Render the requested region of a coverage to the chosen MIME format.</summary>
    /// <param name="ddb">Resolved DDB handle for the dataset.</param>
    /// <param name="layer">Resolved coverage.</param>
    /// <param name="bboxWgs84">[minLon, minLat, maxLon, maxLat] in EPSG:4326.</param>
    /// <param name="width">Target pixel width. Pass 0 to auto-compute from bbox.</param>
    /// <param name="height">Target pixel height. Pass 0 to auto-compute from bbox.</param>
    /// <param name="mime">Output MIME type (e.g. image/tiff, image/png, image/jpeg).</param>
    /// <param name="bands">Optional 1-based band selection (WCS 2.0 RangeSubset /
    /// WCS 1.1 RangeSubset / WCS 1.0 BANDS extension). When non-null the output
    /// preserves exactly these bands in the requested order.</param>
    /// <param name="outputCrs">Optional target CRS authority code (WCS 2.0 OUTPUTCRS /
    /// WCS 1.0 RESPONSE_CRS). Null/empty = EPSG:4326 (current behaviour).</param>
    /// <param name="ct">Cancels waiting for a native render slot (not a running render).</param>
    public Task<byte[]> RenderRegionAsync(IDDB ddb, OgcLayerDto layer, double[] bboxWgs84,
        int width, int height, string mime,
        int[]? bands = null, string? outputCrs = null, CancellationToken ct = default)
    {
        if (bboxWgs84 == null || bboxWgs84.Length < 4)
            throw new OgcException("InvalidParameterValue", "Invalid bbox", 400);
        const int targetMax = 2048;
        const int targetMin = 64;
        // Auto-derive a sensible size when the client does not supply explicit W/H
        // (WCS 2.0 SUBSET, WCS 1.0 missing WIDTH/HEIGHT). 100000 ≈ ~1 m/pixel at the equator.
        if (width <= 0)
            width = (int)Math.Min(targetMax,
                Math.Max(targetMin, (bboxWgs84[2] - bboxWgs84[0]) * 100000));
        if (height <= 0)
            height = (int)Math.Min(targetMax,
                Math.Max(targetMin, (bboxWgs84[3] - bboxWgs84[1]) * 100000));
        width = Math.Clamp(width, targetMin, targetMax);
        height = Math.Clamp(height, targetMin, targetMax);
        var src = ResolveRasterArtifact(ddb, layer);
        var mimeNorm = string.IsNullOrWhiteSpace(mime) ? "image/tiff" : mime;
        // Coverages can be large: they share the Heavy lane with thumbnails
        return _gate.RunAsync(NativeLane.Heavy, () => DdbWrapper.RenderRasterRegion(src, bboxWgs84, "EPSG:4326",
            width, height, mimeNorm, bands, outputCrs), ct);
    }

    /// <summary>Cache key of a WCS document of the dataset (see <c>IOgcLayerCatalog.CacheKeyAsync</c>).</summary>
    public Task<string> CacheKeyAsync(string orgSlug, string dsSlug, string kind, string? detail,
        CancellationToken ct = default) =>
        LayerCatalog.CacheKeyAsync(orgSlug, dsSlug, kind, detail, ct);

    /// <summary>Absolute base URL of the WCS endpoint for the current request (no trailing '?').</summary>
    public string GetBaseUrl(string orgSlug, string dsSlug, string? folderPath)
        => GetServiceUrl(orgSlug, dsSlug, "wcs", folderPath);
}