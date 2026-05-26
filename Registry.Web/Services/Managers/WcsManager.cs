using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;
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
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx, ILogger<WcsManager> logger)
        : base(u, a, d, ar, w, c, cache, ctx)
    {
        _logger = logger;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null)
    {
        var key = $"ogc-caps-wcs-v2-2.0.0-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = (await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath))
            .Where(l => l.EntryType == EntryType.GeoRaster && l.BboxWgs84 != null).ToList();
        var baseUrl = GetServiceUrl(orgSlug, dsSlug, "wcs", folderPath);

        var sb = new StringBuilder();
        await using var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true });
        await w.WriteStartElementAsync("wcs", "Capabilities", "http://www.opengis.net/wcs/2.0");
        await w.WriteAttributeStringAsync("xmlns", "ows", null, "http://www.opengis.net/ows/2.0");
        await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
        w.WriteAttributeString("version", "2.0.1");

        await w.WriteStartElementAsync("ows", "ServiceIdentification", "http://www.opengis.net/ows/2.0");
        await w.WriteElementStringAsync("ows", "Title", "http://www.opengis.net/ows/2.0", $"DroneDB WCS — {orgSlug}/{dsSlug}");
        await w.WriteElementStringAsync("ows", "ServiceType", "http://www.opengis.net/ows/2.0", "OGC WCS");
        await w.WriteElementStringAsync("ows", "ServiceTypeVersion", "http://www.opengis.net/ows/2.0", "2.0.1");
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync("ows", "OperationsMetadata", "http://www.opengis.net/ows/2.0");
        foreach (var op in new[] { "GetCapabilities", "DescribeCoverage", "GetCoverage" })
        {
            await w.WriteStartElementAsync("ows", "Operation", "http://www.opengis.net/ows/2.0");
            w.WriteAttributeString("name", op);
            await w.WriteStartElementAsync("ows", "DCP", "http://www.opengis.net/ows/2.0");
            await w.WriteStartElementAsync("ows", "HTTP", "http://www.opengis.net/ows/2.0");
            await w.WriteStartElementAsync("ows", "Get", "http://www.opengis.net/ows/2.0");
            w.WriteAttributeString("xlink", "href", NsXlink, baseUrl + "?");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync(); // OperationsMetadata

        await w.WriteStartElementAsync("wcs", "ServiceMetadata", "http://www.opengis.net/wcs/2.0");
        await w.WriteElementStringAsync("wcs", "formatSupported", "http://www.opengis.net/wcs/2.0", "image/tiff");
        await w.WriteEndElementAsync();

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

