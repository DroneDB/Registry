using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>WCS 2.0 coverage manager (single-coverage subset → GeoTIFF).</summary>
public class WcsManager : OgcManagerBase, IWcsManager
{
    private readonly ILogger<WcsManager> _logger;

    public WcsManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, ILogger<WcsManager> logger)
        : base(u, a, d, ar, w, c, cache)
    {
        _logger = logger;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null)
    {
        var key = $"ogc-caps-wcs-2.0.0-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = (await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath))
            .Where(l => l.EntryType == EntryType.GeoRaster && l.BboxWgs84 != null).ToList();

        var sb = new StringBuilder();
        await using var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true });
        await w.WriteStartElementAsync("wcs", "Capabilities", "http://www.opengis.net/wcs/2.0");
        await w.WriteAttributeStringAsync("xmlns", "ows", null, "http://www.opengis.net/ows/2.0");
        w.WriteAttributeString("version", "2.0.1");
        await w.WriteStartElementAsync("wcs", "Contents", null);
        foreach (var l in layers)
        {
            await w.WriteStartElementAsync("wcs", "CoverageSummary", null);
            await w.WriteElementStringAsync("wcs", "CoverageId", null, EncodeId(l.Name));
            await w.WriteElementStringAsync("wcs", "CoverageSubtype", null, "RectifiedGridCoverage");
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        var xml = Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
        await Cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    public async Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string coverageId)
    {
        var name = DecodeId(coverageId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("NoSuchCoverage", $"Coverage '{coverageId}' not found", 404);

        var sb = new StringBuilder();
        await using var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true });
        await w.WriteStartElementAsync("wcs", "CoverageDescriptions", "http://www.opengis.net/wcs/2.0");
        await w.WriteStartElementAsync("wcs", "CoverageDescription", null);
        w.WriteAttributeString("id", coverageId);
        if (layer.BboxWgs84 != null)
        {
            w.WriteStartElement("boundedBy");
            w.WriteStartElement("Envelope");
            w.WriteAttributeString("srsName", "EPSG:4326");
            w.WriteElementString("lowerCorner",
                FormattableString.Invariant($"{layer.BboxWgs84[0]} {layer.BboxWgs84[1]}"));
            w.WriteElementString("upperCorner",
                FormattableString.Invariant($"{layer.BboxWgs84[2]} {layer.BboxWgs84[3]}"));
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }

    public async Task<byte[]> GetCoverageAsync(string orgSlug, string dsSlug, string coverageId,
        double[]? subsetBbox, string format)
    {
        var name = DecodeId(coverageId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("NoSuchCoverage", $"Coverage '{coverageId}' not found", 404);
        if (layer.EntryType != EntryType.GeoRaster)
            throw new OgcException("InvalidParameterValue", "WCS supports only raster coverages", 400);

        var bbox = subsetBbox ?? layer.BboxWgs84
                   ?? throw new OgcException("InvalidParameterValue", "Coverage has no BBOX", 400);
        var src = ResolveRasterArtifact(ddb, layer);
        // Honour requested FORMAT. WCS 2.0 default is image/tiff (GeoTIFF, georeferenced).
        // libddb's renderRasterRegion supports tiff/png/jpeg/webp via GDAL warp.
        var mime = string.IsNullOrWhiteSpace(format) ? "image/tiff" : format;
        const int targetMax = 2048;
        var w = (int)Math.Min(targetMax, Math.Max(64, (bbox[2] - bbox[0]) * 100000));
        var h = (int)Math.Min(targetMax, Math.Max(64, (bbox[3] - bbox[1]) * 100000));
        return DdbWrapper.RenderRasterRegion(src, bbox, "EPSG:4326", w, h, mime);
    }

    private static string EncodeId(string raw) => Uri.EscapeDataString(raw);
    private static string DecodeId(string enc) => Uri.UnescapeDataString(enc);
}

/// <summary>OGC API – Features manager (JSON-based REST).</summary>
public class OgcApiFeaturesManager : OgcManagerBase, IOgcApiFeaturesManager
{
    private readonly ILogger<OgcApiFeaturesManager> _logger;

    public OgcApiFeaturesManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, ILogger<OgcApiFeaturesManager> logger)
        : base(u, a, d, ar, w, c, cache)
    {
        _logger = logger;
    }

    public Task<OgcConformanceDto> GetConformanceAsync() => Task.FromResult(new OgcConformanceDto());

    public async Task<OgcApiLandingDto> GetLandingAsync(string orgSlug, string dsSlug, string baseUrl)
    {
        await ResolveAsync(orgSlug, dsSlug);
        var b = baseUrl.TrimEnd('/');
        return new OgcApiLandingDto
        {
            Title = $"OGC API for {orgSlug}/{dsSlug}",
            Links = new()
            {
                new() { Href = $"{b}/", Rel = "self", Type = "application/json" },
                new() { Href = $"{b}/conformance", Rel = "conformance", Type = "application/json" },
                new() { Href = $"{b}/collections", Rel = "data", Type = "application/json" }
            }
        };
    }

    public async Task<OgcApiCollectionsDto> GetCollectionsAsync(string orgSlug, string dsSlug, string baseUrl)
    {
        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug);
        var b = baseUrl.TrimEnd('/');
        var dto = new OgcApiCollectionsDto
        {
            Links = new() { new() { Href = $"{b}/collections", Rel = "self", Type = "application/json" } }
        };
        foreach (var l in layers)
        {
            dto.Collections.Add(BuildCollection(l, b));
        }
        return dto;
    }

    public async Task<OgcApiCollectionDto?> GetCollectionAsync(string orgSlug, string dsSlug,
        string collectionId, string baseUrl)
    {
        var name = Uri.UnescapeDataString(collectionId);
        await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name);
        return layer == null ? null : BuildCollection(layer, baseUrl.TrimEnd('/'));
    }

    public async Task<string> GetItemsAsync(string orgSlug, string dsSlug, string collectionId,
        double[]? bbox, int limit, int offset)
    {
        if (limit <= 0) limit = 10;
        limit = Math.Clamp(limit, 1, 1000);
        if (offset < 0) offset = 0;

        var name = Uri.UnescapeDataString(collectionId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404, "collectionId");
        if (layer.EntryType != EntryType.Vector)
            throw new OgcException("OperationNotSupported",
                "OGC API – Features supports only Vector layers", 400);

        var gpkg = ResolveVectorArtifact(ddb, layer);
        return DdbWrapper.QueryVector(gpkg, layer.InnerLayerName, bbox, bbox != null ? "EPSG:4326" : null,
            limit, offset, "application/json");
    }

    public async Task<string> GetItemAsync(string orgSlug, string dsSlug, string collectionId, string featureId)
    {
        // Best-effort: pull a single page of features and filter client-side by 'id'/'fid' property.
        var items = await GetItemsAsync(orgSlug, dsSlug, collectionId, bbox: null, limit: 1000, offset: 0);
        try
        {
            var fc = JObject.Parse(items);
            var features = fc["features"] as JArray;
            if (features == null) return "{}";
            var match = features.OfType<JObject>().FirstOrDefault(f =>
                f["id"]?.ToString() == featureId
                || (f["properties"] is JObject p && p["fid"]?.ToString() == featureId));
            return match?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
        }
        catch
        {
            return "{}";
        }
    }

    private static OgcApiCollectionDto BuildCollection(OgcLayerDto layer, string baseUrl)
    {
        var id = Uri.EscapeDataString(layer.Name);
        return new OgcApiCollectionDto
        {
            Id = id,
            Title = layer.Title,
            ItemType = layer.EntryType == EntryType.Vector ? "feature" : "coverage",
            Extent = layer.BboxWgs84 != null
                ? new OgcApiExtentDto
                {
                    Spatial = new OgcApiSpatialExtentDto { Bbox = new[] { layer.BboxWgs84 } }
                }
                : null,
            Links = new()
            {
                new() { Href = $"{baseUrl}/collections/{id}", Rel = "self", Type = "application/json" },
                new() { Href = $"{baseUrl}/collections/{id}/items", Rel = "items",
                        Type = "application/geo+json" }
            }
        };
    }
}

/// <summary>OGC API – Tiles manager: thin redirect/proxy onto MVT (vector) or XYZ (raster).</summary>
public class OgcApiTilesManager : OgcManagerBase, IOgcApiTilesManager
{
    public OgcApiTilesManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache)
        : base(u, a, d, ar, w, c, cache) { }

    public async Task<object> GetTileSetsAsync(string orgSlug, string dsSlug, string collectionId, string baseUrl)
    {
        var name = Uri.UnescapeDataString(collectionId);
        await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404);

        var b = baseUrl.TrimEnd('/');
        return new
        {
            tilesets = new[]
            {
                new
                {
                    tileMatrixSetURI = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad",
                    links = new[]
                    {
                        new { href = $"{b}/collections/{collectionId}/tiles/WebMercatorQuad/{{z}}/{{y}}/{{x}}",
                              rel = "item",
                              type = layer.EntryType == EntryType.Vector
                                  ? "application/vnd.mapbox-vector-tile"
                                  : "image/png" }
                    }
                }
            }
        };
    }

    public async Task<byte[]?> GetTileAsync(string orgSlug, string dsSlug, string collectionId,
        string tileMatrixSet, int z, int x, int y)
    {
        var name = Uri.UnescapeDataString(collectionId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404);

        if (layer.EntryType == EntryType.Vector)
        {
            var path = Artifacts.GetMvtTilePath(ddb, layer.EntryHash, z, x, y);
            return Artifacts.ArtifactExists(path) ? await File.ReadAllBytesAsync(path) : null;
        }
        return ddb.GenerateTile(layer.EntryPath, z, x, y, retina: false, inputPathHash: layer.EntryHash);
    }
}
