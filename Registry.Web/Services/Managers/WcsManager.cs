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
        var key = $"ogc-caps-wcs-v6-{orgSlug}-{dsSlug}-{folderPath ?? ""}";  // v6: advertise multi-format support
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
        // Advertise the implemented WCS 2.0 conformance classes (required by OGC CITE ets-wcs20).
        foreach (var profile in WcsConformance.Profiles)
            await w.WriteElementStringAsync("ows", "Profile", "http://www.opengis.net/ows/2.0", profile);
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync("ows", "OperationsMetadata", "http://www.opengis.net/ows/2.0");
        foreach (var op in new[] { "GetCapabilities", "DescribeCoverage", "GetCoverage" })
        {
            await w.WriteStartElementAsync("ows", "Operation", "http://www.opengis.net/ows/2.0");
            w.WriteAttributeString("name", op);
            await w.WriteStartElementAsync("ows", "DCP", "http://www.opengis.net/ows/2.0");
            await w.WriteStartElementAsync("ows", "HTTP", "http://www.opengis.net/ows/2.0");
            await w.WriteStartElementAsync("ows", "Get", "http://www.opengis.net/ows/2.0");
            await w.WriteAttributeStringAsync("xlink", "href", NsXlink, baseUrl + "?");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync(); // OperationsMetadata

        await w.WriteStartElementAsync("wcs", "ServiceMetadata", "http://www.opengis.net/wcs/2.0");
        foreach (var f in WcsConformance.SupportedFormats)
            await w.WriteElementStringAsync("wcs", "formatSupported", "http://www.opengis.net/wcs/2.0", f);
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync("wcs", "Contents", null);
        foreach (var l in layers)
        {
            await w.WriteStartElementAsync("wcs", "CoverageSummary", null);
            // WCS 2.0 xsd: CoverageId is xs:NCName — must not start with a digit or contain spaces.
            await w.WriteElementStringAsync("wcs", "CoverageId", null, OgcNames.ToNcName(l.Name));
            await w.WriteElementStringAsync("wcs", "CoverageSubtype", null, "RectifiedGridCoverage");
            if (l.BboxWgs84 != null)
            {
                await w.WriteStartElementAsync("ows", "WGS84BoundingBox", "http://www.opengis.net/ows/2.0");
                await w.WriteElementStringAsync("ows", "LowerCorner", "http://www.opengis.net/ows/2.0",
                    FormattableString.Invariant($"{l.BboxWgs84[0]} {l.BboxWgs84[1]}"));
                await w.WriteElementStringAsync("ows", "UpperCorner", "http://www.opengis.net/ows/2.0",
                    FormattableString.Invariant($"{l.BboxWgs84[2]} {l.BboxWgs84[3]}"));
                await w.WriteEndElementAsync(); // ows:WGS84BoundingBox
            }
            await w.WriteEndElementAsync(); // wcs:CoverageSummary
        }
        await w.WriteEndElementAsync(); // wcs:Contents
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        var xml = Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
        await Cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    public async Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string coverageId)
    {
        // WCS 2.0 (OGC 09-110r4 §9.3.2.1): CoverageId may be a comma-separated list; the
        // response wcs:CoverageDescriptions contains one wcs:CoverageDescription per id.
        // Any unknown id => single ows:Exception with code NoSuchCoverage listing the bad ids.
        var ids = (coverageId ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => DecodeId(s).Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        if (ids.Length == 0)
            throw new OgcException("MissingParameterValue", "CoverageId is required", 400, "CoverageId");

        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var resolved = new (string Id, OgcLayerDto Layer)[ids.Length];
        var missing = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, ids[i]);
            if (layer == null) missing.Add(ids[i]); else resolved[i] = (ids[i], layer);
        }
        if (missing.Count > 0)
            throw new OgcException("NoSuchCoverage",
                $"Coverage{(missing.Count > 1 ? "s" : "")} '{string.Join(",", missing)}' not found",
                404, "CoverageId");

        const string nsWcs = "http://www.opengis.net/wcs/2.0";
        const string nsGml = "http://www.opengis.net/gml/3.2";
        const string nsOws = "http://www.opengis.net/ows/2.0";
        const string nsGmlcov = "http://www.opengis.net/gmlcov/1.0";
        const string nsSwe = "http://www.opengis.net/swe/2.0";

        var sb = new StringBuilder();
        await using var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true });
        await w.WriteStartElementAsync("wcs", "CoverageDescriptions", nsWcs);
        await w.WriteAttributeStringAsync("xmlns", "gml", null, nsGml);
        await w.WriteAttributeStringAsync("xmlns", "ows", null, nsOws);
        await w.WriteAttributeStringAsync("xmlns", "gmlcov", null, nsGmlcov);
        await w.WriteAttributeStringAsync("xmlns", "swe", null, nsSwe);

        foreach (var (id, layer) in resolved)
            await WriteCoverageDescriptionAsync(w, ddb, id, layer, nsWcs, nsGml, nsGmlcov, nsSwe);

        await w.WriteEndElementAsync(); // wcs:CoverageDescriptions
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }

    /// <summary>Emit one wcs:CoverageDescription element for the given layer.</summary>
    private async Task WriteCoverageDescriptionAsync(XmlWriter w, IDDB ddb, string id, OgcLayerDto layer,
        string nsWcs, string nsGml, string nsGmlcov, string nsSwe)
    {
        var gmlId = OgcNames.ToNcName(id);

        // Probe raster for grid size / band metadata so the synthesized gml:RectifiedGrid and
        // gmlcov:rangeType reflect the actual coverage geometry.
        int rWidth = 0, rHeight = 0, rBands = 0;
        var bandNames = new List<string>();
        try
        {
            var src = ResolveRasterArtifact(ddb, layer);
            var info = DdbWrapper.GetRasterInfo(src);
            if (!string.IsNullOrEmpty(info))
            {
                var j = JObject.Parse(info);
                rWidth  = j.Value<int?>("width")  ?? 0;
                rHeight = j.Value<int?>("height") ?? 0;
                rBands  = j.Value<int?>("bandCount") ?? 0;
                if (j["bands"] is JArray ba)
                    foreach (var b in ba)
                        bandNames.Add(b.Value<string>("name") ?? $"band{bandNames.Count + 1}");
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "WCS DescribeCoverage: probing raster info failed"); }
        if (rWidth  <= 0) rWidth  = 1024;
        if (rHeight <= 0) rHeight = 1024;
        if (rBands  <= 0) rBands  = Math.Max(1, layer.BandCount);
        while (bandNames.Count < rBands) bandNames.Add($"band{bandNames.Count + 1}");

        await w.WriteStartElementAsync("wcs", "CoverageDescription", null);
        // gml:id must be an NCName; wcs:CoverageId carries the (already-sanitized) name.
        await w.WriteAttributeStringAsync("gml", "id", nsGml, gmlId);

        const string srs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
        var bbox = layer.BboxWgs84 ?? [-180.0, -90.0, 180.0, 90.0];
        await w.WriteStartElementAsync("gml", "boundedBy", nsGml);
        await w.WriteStartElementAsync("gml", "Envelope", nsGml);
        w.WriteAttributeString("srsName", srs);
        w.WriteAttributeString("axisLabels", "Long Lat");
        w.WriteAttributeString("uomLabels", "deg deg");
        w.WriteAttributeString("srsDimension", "2");
        await w.WriteElementStringAsync("gml", "lowerCorner", nsGml,
            FormattableString.Invariant($"{bbox[0]} {bbox[1]}"));
        await w.WriteElementStringAsync("gml", "upperCorner", nsGml,
            FormattableString.Invariant($"{bbox[2]} {bbox[3]}"));
        await w.WriteEndElementAsync(); // gml:Envelope
        await w.WriteEndElementAsync(); // gml:boundedBy

        await w.WriteElementStringAsync("wcs", "CoverageId", null, gmlId);

        await w.WriteStartElementAsync("gml", "domainSet", nsGml);
        await w.WriteStartElementAsync("gml", "RectifiedGrid", nsGml);
        await w.WriteAttributeStringAsync("gml", "id", nsGml, gmlId + "_grid");
        w.WriteAttributeString("dimension", "2");
        await w.WriteStartElementAsync("gml", "limits", nsGml);
        await w.WriteStartElementAsync("gml", "GridEnvelope", nsGml);
        await w.WriteElementStringAsync("gml", "low", nsGml, "0 0");
        await w.WriteElementStringAsync("gml", "high", nsGml,
            FormattableString.Invariant($"{rWidth - 1} {rHeight - 1}"));
        await w.WriteEndElementAsync(); // gml:GridEnvelope
        await w.WriteEndElementAsync(); // gml:limits
        await w.WriteElementStringAsync("gml", "axisLabels", nsGml, "Long Lat");
        await w.WriteStartElementAsync("gml", "origin", nsGml);
        await w.WriteStartElementAsync("gml", "Point", nsGml);
        await w.WriteAttributeStringAsync("gml", "id", nsGml, gmlId + "_origin");
        w.WriteAttributeString("srsName", srs);
        await w.WriteElementStringAsync("gml", "pos", nsGml,
            FormattableString.Invariant($"{bbox[0]} {bbox[3]}"));
        await w.WriteEndElementAsync(); // gml:Point
        await w.WriteEndElementAsync(); // gml:origin
        var dx = (bbox[2] - bbox[0]) / Math.Max(1, rWidth);
        var dy = (bbox[3] - bbox[1]) / Math.Max(1, rHeight);
        await w.WriteStartElementAsync("gml", "offsetVector", nsGml);
        w.WriteAttributeString("srsName", srs);
        await w.WriteStringAsync(FormattableString.Invariant($"{dx} 0"));
        await w.WriteEndElementAsync();
        await w.WriteStartElementAsync("gml", "offsetVector", nsGml);
        w.WriteAttributeString("srsName", srs);
        await w.WriteStringAsync(FormattableString.Invariant($"0 {-dy}"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync(); // gml:RectifiedGrid
        await w.WriteEndElementAsync(); // gml:domainSet

        await w.WriteStartElementAsync("gmlcov", "rangeType", nsGmlcov);
        await w.WriteStartElementAsync("swe", "DataRecord", nsSwe);
        for (var i = 0; i < rBands; i++)
        {
            var bn = OgcNames.ToNcName(bandNames[i]);
            await w.WriteStartElementAsync("swe", "field", nsSwe);
            w.WriteAttributeString("name", bn);
            await w.WriteStartElementAsync("swe", "Quantity", nsSwe);
            await w.WriteStartElementAsync("swe", "uom", nsSwe);
            w.WriteAttributeString("code", "10^0");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync(); // swe:Quantity
            await w.WriteEndElementAsync(); // swe:field
        }
        await w.WriteEndElementAsync(); // swe:DataRecord
        await w.WriteEndElementAsync(); // gmlcov:rangeType

        await w.WriteStartElementAsync("wcs", "ServiceParameters", null);
        await w.WriteElementStringAsync("wcs", "CoverageSubtype", null, "RectifiedGridCoverage");
        await w.WriteElementStringAsync("wcs", "nativeFormat", null, "image/tiff");
        await w.WriteEndElementAsync(); // wcs:ServiceParameters

        await w.WriteEndElementAsync(); // wcs:CoverageDescription
    }

    public async Task<byte[]> GetCoverageAsync(string orgSlug, string dsSlug, string coverageId,
        IReadOnlyCollection<string> subsetParams, string format)
    {
        var name = DecodeId(coverageId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("NoSuchCoverage", $"Coverage '{coverageId}' not found", 404);
        if (layer.EntryType != EntryType.GeoRaster)
            throw new OgcException("InvalidParameterValue", "WCS supports only raster coverages", 400);

        var coverageBbox = layer.BboxWgs84
                   ?? throw new OgcException("InvalidParameterValue", "Coverage has no BBOX", 400);
        // Parses + validates SUBSET per WCS 2.0 Req30/31/32/33; raises typed OgcExceptions.
        var bbox = WcsSubsetParser.Parse(subsetParams, coverageBbox) ?? coverageBbox;
        var src = ResolveRasterArtifact(ddb, layer);
        // Honour requested FORMAT. WCS 2.0 default is image/tiff (GeoTIFF, georeferenced).
        // libddb's renderRasterRegion supports tiff/png/jpeg/webp via GDAL warp.
        var mime = string.IsNullOrWhiteSpace(format) ? "image/tiff" : format;
        const int targetMax = 2048;
        var w = (int)Math.Min(targetMax, Math.Max(64, (bbox[2] - bbox[0]) * 100000));
        var h = (int)Math.Min(targetMax, Math.Max(64, (bbox[3] - bbox[1]) * 100000));
        return DdbWrapper.RenderRasterRegion(src, bbox, "EPSG:4326", w, h, mime);
    }

    // CoverageId on the wire is an xs:NCName (see SanitizeFeatureName). DescribeCoverage /
    // GetCoverage receive that NCName and rely on IOgcLayerCatalog.ResolveAsync, which already
    // falls back to a NCName-based match (see OgcLayerCatalog.ResolveAsync). Decode therefore
    // only needs to undo percent-encoding from the HTTP query string.
    private static string DecodeId(string enc) => Uri.UnescapeDataString(enc);
}

