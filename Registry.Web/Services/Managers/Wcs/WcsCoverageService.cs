using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>
/// Shared domain operations used by every <see cref="IWcsProtocolHandler"/>.
/// Inherits <see cref="OgcManagerBase"/> only to reuse <c>ResolveAsync</c>,
/// <c>ResolveRasterArtifact</c>, <c>GetServiceUrl</c> and the cache; nothing
/// version-specific lives here.
/// </summary>
public class WcsCoverageService : OgcManagerBase, IWcsCoverageService
{
    private readonly ILogger<WcsCoverageService> _logger;

    public WcsCoverageService(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx,
        ILogger<WcsCoverageService> logger) : base(u, a, d, ar, w, c, cache, ctx)
    {
        _logger = logger;
    }

    public async Task<(IDDB Ddb, IReadOnlyList<OgcLayerDto> Layers)> GetCoveragesAsync(
        string orgSlug, string dsSlug, string? folderPath)
    {
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layers = (await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath))
            .Where(l => l.EntryType == EntryType.GeoRaster && l.BboxWgs84 != null)
            .ToList();
        return (ddb, layers);
    }

    public async Task<(IDDB Ddb, OgcLayerDto Layer)> ResolveCoverageAsync(
        string orgSlug, string dsSlug, string coverageId)
    {
        var name = Uri.UnescapeDataString(coverageId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("NoSuchCoverage",
                        $"Coverage '{coverageId}' not found", 404);
        if (layer.EntryType != EntryType.GeoRaster)
            throw new OgcException("InvalidParameterValue",
                "WCS supports only raster coverages", 400);
        return (ddb, layer);
    }

    public async Task<(IDDB Ddb, IReadOnlyList<(string Id, OgcLayerDto Layer)> Coverages)> ResolveCoveragesAsync(
        string orgSlug, string dsSlug, IEnumerable<string> coverageIds)
    {
        var ids = coverageIds
            .Select(s => Uri.UnescapeDataString(s ?? string.Empty).Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (ids.Length == 0)
            throw new OgcException("MissingParameterValue", "CoverageId is required", 400, "CoverageId");

        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var resolved = new List<(string Id, OgcLayerDto Layer)>(ids.Length);
        var missing = new List<string>();
        foreach (var id in ids)
        {
            var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, id);
            if (layer == null) missing.Add(id);
            else resolved.Add((id, layer));
        }

        if (missing.Count > 0)
            throw new OgcException("NoSuchCoverage",
                $"Coverage{(missing.Count > 1 ? "s" : "")} '{string.Join(",", missing)}' not found",
                404, "CoverageId");
        return (ddb, resolved);
    }

    public WcsRasterInfo ProbeRaster(IDDB ddb, OgcLayerDto layer)
    {
        try
        {
            var src = ResolveRasterArtifact(ddb, layer);
            var info = DdbWrapper.GetRasterInfo(src);
            if (string.IsNullOrEmpty(info)) return WcsRasterInfo.Empty;
            var j = JObject.Parse(info);
            var width = j.Value<int?>("width") ?? 0;
            var height = j.Value<int?>("height") ?? 0;
            var bandCount = j.Value<int?>("bandCount") ?? 0;
            var bandNames = new List<string>();

            if (j["bands"] is not JArray ba)
                return new WcsRasterInfo(width, height, bandCount, bandNames);

            foreach (var b in ba)
                bandNames.Add(b.Value<string>("name") ?? $"band{bandNames.Count + 1}");
            return new WcsRasterInfo(width, height, bandCount, bandNames);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WCS: probing raster info failed");
            return WcsRasterInfo.Empty;
        }
    }

    public byte[] RenderRegion(IDDB ddb, OgcLayerDto layer, double[] bboxWgs84,
        int width, int height, string mime)
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
        return DdbWrapper.RenderRasterRegion(src, bboxWgs84, "EPSG:4326", width, height, mimeNorm);
    }

    public string GetBaseUrl(string orgSlug, string dsSlug, string? folderPath)
        => GetServiceUrl(orgSlug, dsSlug, "wcs", folderPath);
}