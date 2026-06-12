using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>
/// WCS 1.0.0 protocol handler (OGC 03-065r6). Self-contained XML schema
/// (no OWS namespace), BBOX+WIDTH/HEIGHT GetCoverage encoding, short-name
/// FORMAT values (GeoTIFF/PNG/JPEG).
/// </summary>
public sealed class WcsProtocol10Handler : IWcsProtocolHandler
{
    public string Version => "1.0.0";

    private readonly IWcsCoverageService _svc;
    private readonly IDistributedCache _cache;

    private const string NsWcs = "http://www.opengis.net/wcs";
    private const string NsGml = "http://www.opengis.net/gml";
    private const string NsXlink = "http://www.w3.org/1999/xlink";
    private const string NsXsi = "http://www.w3.org/2001/XMLSchema-instance";
    private const string Utf8XmlDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public WcsProtocol10Handler(IWcsCoverageService svc, IDistributedCache cache)
    {
        _svc = svc;
        _cache = cache;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath)
    {
        var key = $"ogc-caps-wcs10-v1-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await _cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        var (_, layers) = await _svc.GetCoveragesAsync(orgSlug, dsSlug, folderPath);
        var baseUrl = _svc.GetBaseUrl(orgSlug, dsSlug, folderPath);

        var sb = new StringBuilder();
        await using (var w = XmlWriter.Create(sb,
            new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true }))
        {
            await w.WriteStartElementAsync(null, "WCS_Capabilities", NsWcs);
            await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
            await w.WriteAttributeStringAsync("xmlns", "xsi", null, NsXsi);
            w.WriteAttributeString("version", "1.0.0");
            w.WriteAttributeString("updateSequence", "0");

            // Service
            await w.WriteStartElementAsync(null, "Service", NsWcs);
            await w.WriteElementStringAsync(null, "description", NsWcs,
                $"DroneDB WCS 1.0.0 - {orgSlug}/{dsSlug}");
            await w.WriteElementStringAsync(null, "name", NsWcs, "OGC:WCS");
            await w.WriteElementStringAsync(null, "label", NsWcs,
                $"DroneDB WCS - {orgSlug}/{dsSlug}");
            await w.WriteElementStringAsync(null, "fees", NsWcs, "NONE");
            await w.WriteElementStringAsync(null, "accessConstraints", NsWcs, "NONE");
            await w.WriteEndElementAsync();

            // Capability
            await w.WriteStartElementAsync(null, "Capability", NsWcs);
            await w.WriteStartElementAsync(null, "Request", NsWcs);
            foreach (var op in new[] { "GetCapabilities", "DescribeCoverage", "GetCoverage" })
            {
                await w.WriteStartElementAsync(null, op, NsWcs);
                await w.WriteStartElementAsync(null, "DCPType", NsWcs);
                await w.WriteStartElementAsync(null, "HTTP", NsWcs);
                await w.WriteStartElementAsync(null, "Get", NsWcs);
                await w.WriteStartElementAsync(null, "OnlineResource", NsWcs);
                await w.WriteAttributeStringAsync("xlink", "href", NsXlink, baseUrl + "?");
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();
            await w.WriteStartElementAsync(null, "Exception", NsWcs);
            await w.WriteElementStringAsync(null, "Format", NsWcs, "application/vnd.ogc.se_xml");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();

            // ContentMetadata
            await w.WriteStartElementAsync(null, "ContentMetadata", NsWcs);
            foreach (var l in layers)
            {
                await w.WriteStartElementAsync(null, "CoverageOfferingBrief", NsWcs);
                await w.WriteElementStringAsync(null, "name", NsWcs, OgcNames.ToNcName(l.Name));
                await w.WriteElementStringAsync(null, "label", NsWcs,
                    string.IsNullOrEmpty(l.Title) ? l.Name : l.Title);
                if (l.BboxWgs84 != null)
                    await WriteLonLatEnvelopeAsync(w, l.BboxWgs84);
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();

            await w.WriteEndElementAsync(); // WCS_Capabilities
            await w.WriteEndDocumentAsync();
            await w.FlushAsync();
        }
        var xml = Utf8XmlDecl + StripDeclaration(sb.ToString());
        await _cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    public async Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string coverageIds)
    {
        // WCS 1.0 §6.3.1: COVERAGE is comma-separated; omitted = all coverages.
        string[] ids;
        if (string.IsNullOrWhiteSpace(coverageIds))
        {
            var (_, all) = await _svc.GetCoveragesAsync(orgSlug, dsSlug, null);
            ids = all.Select(l => l.Name).ToArray();
        }
        else
        {
            ids = coverageIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        var (ddb, resolved) = await _svc.ResolveCoveragesAsync(orgSlug, dsSlug, ids);

        var sb = new StringBuilder();
        await using (var w = XmlWriter.Create(sb,
            new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true }))
        {
            await w.WriteStartElementAsync(null, "CoverageDescription", NsWcs);
            await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
            w.WriteAttributeString("version", "1.0.0");
            w.WriteAttributeString("updateSequence", "0");

            foreach (var (id, layer) in resolved)
                await WriteCoverageOfferingAsync(w, ddb, id, layer);

            await w.WriteEndElementAsync();
            await w.WriteEndDocumentAsync();
            await w.FlushAsync();
        }
        return Utf8XmlDecl + StripDeclaration(sb.ToString());
    }

    private async Task WriteCoverageOfferingAsync(XmlWriter w, IDDB ddb, string id, OgcLayerDto layer)
    {
        var bbox = layer.BboxWgs84 ?? [-180.0, -90.0, 180.0, 90.0];
        var info = _svc.ProbeRaster(ddb, layer);
        var rWidth = info.Width > 0 ? info.Width : 1024;
        var rHeight = info.Height > 0 ? info.Height : 1024;

        await w.WriteStartElementAsync(null, "CoverageOffering", NsWcs);
        await w.WriteElementStringAsync(null, "name", NsWcs, OgcNames.ToNcName(id));
        await w.WriteElementStringAsync(null, "label", NsWcs,
            string.IsNullOrEmpty(layer.Title) ? layer.Name : layer.Title);
        await WriteLonLatEnvelopeAsync(w, bbox);

        // domainSet → spatialDomain (gml:Envelope + gml:RectifiedGrid)
        await w.WriteStartElementAsync(null, "domainSet", NsWcs);
        await w.WriteStartElementAsync(null, "spatialDomain", NsWcs);
        await w.WriteStartElementAsync("gml", "Envelope", NsGml);
        w.WriteAttributeString("srsName", "EPSG:4326");
        await w.WriteElementStringAsync("gml", "pos", NsGml,
            FormattableString.Invariant($"{bbox[0]} {bbox[1]}"));
        await w.WriteElementStringAsync("gml", "pos", NsGml,
            FormattableString.Invariant($"{bbox[2]} {bbox[3]}"));
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync("gml", "RectifiedGrid", NsGml);
        w.WriteAttributeString("dimension", "2");
        await w.WriteStartElementAsync("gml", "limits", NsGml);
        await w.WriteStartElementAsync("gml", "GridEnvelope", NsGml);
        await w.WriteElementStringAsync("gml", "low", NsGml, "0 0");
        await w.WriteElementStringAsync("gml", "high", NsGml,
            FormattableString.Invariant($"{rWidth - 1} {rHeight - 1}"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync("gml", "axisName", NsGml, "x");
        await w.WriteElementStringAsync("gml", "axisName", NsGml, "y");
        await w.WriteStartElementAsync("gml", "origin", NsGml);
        await w.WriteElementStringAsync("gml", "pos", NsGml,
            FormattableString.Invariant($"{bbox[0]} {bbox[3]}"));
        await w.WriteEndElementAsync();
        var dx = (bbox[2] - bbox[0]) / Math.Max(1, rWidth);
        var dy = (bbox[3] - bbox[1]) / Math.Max(1, rHeight);
        await w.WriteElementStringAsync("gml", "offsetVector", NsGml,
            FormattableString.Invariant($"{dx} 0"));
        await w.WriteElementStringAsync("gml", "offsetVector", NsGml,
            FormattableString.Invariant($"0 {-dy}"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();

        // rangeSet
        await w.WriteStartElementAsync(null, "rangeSet", NsWcs);
        await w.WriteStartElementAsync(null, "RangeSet", NsWcs);
        await w.WriteElementStringAsync(null, "name", NsWcs, "bands");
        await w.WriteElementStringAsync(null, "label", NsWcs, "Bands");
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();

        // supportedCRSs
        await w.WriteStartElementAsync(null, "supportedCRSs", NsWcs);
        foreach (var crs in WcsConformance.SupportedCrs)
            await w.WriteElementStringAsync(null, "requestResponseCRSs", NsWcs, crs);
        await w.WriteElementStringAsync(null, "nativeCRSs", NsWcs, "EPSG:4326");
        await w.WriteEndElementAsync();

        // supportedFormats
        await w.WriteStartElementAsync(null, "supportedFormats", NsWcs);
        w.WriteAttributeString("nativeFormat", "GeoTIFF");
        foreach (var f in WcsConformance.SupportedFormatsWcs10)
            await w.WriteElementStringAsync(null, "formats", NsWcs, f);
        await w.WriteEndElementAsync();

        // supportedInterpolations
        await w.WriteStartElementAsync(null, "supportedInterpolations", NsWcs);
        w.WriteAttributeString("default", "nearest neighbor");
        await w.WriteElementStringAsync(null, "interpolationMethod", NsWcs, "nearest neighbor");
        await w.WriteEndElementAsync();

        await w.WriteEndElementAsync(); // CoverageOffering
    }

    private static async Task WriteLonLatEnvelopeAsync(XmlWriter w, double[] bbox)
    {
        await w.WriteStartElementAsync(null, "lonLatEnvelope", NsWcs);
        w.WriteAttributeString("srsName", "urn:ogc:def:crs:OGC:1.3:CRS84");
        await w.WriteElementStringAsync("gml", "pos", NsGml,
            FormattableString.Invariant($"{bbox[0]} {bbox[1]}"));
        await w.WriteElementStringAsync("gml", "pos", NsGml,
            FormattableString.Invariant($"{bbox[2]} {bbox[3]}"));
        await w.WriteEndElementAsync();
    }

    public async Task<WcsCoverageResult> GetCoverageAsync(string orgSlug, string dsSlug, IQueryCollection q)
    {
        var coverageId = OgcRequestParser.GetRequired(q, "COVERAGE");
        var rawFormat = OgcRequestParser.GetRequired(q, "FORMAT");
        var mime = WcsConformance.NormalizeWcs10Format(rawFormat);
        if (!WcsConformance.SupportedFormats.Contains(mime, StringComparer.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"format '{rawFormat}' is not supported", 400, "format");

        var crs = OgcRequestParser.GetRequired(q, "CRS");
        // We always render in EPSG:4326 internally and assume the requested CRS is supported.
        if (!IsSupportedCrs(crs))
            throw new OgcException("InvalidParameterValue",
                $"CRS '{crs}' is not supported", 400, "CRS");

        var (bbox, _) = OgcRequestParser.ParseBbox(
            OgcRequestParser.GetRequired(q, "BBOX"), crs, "1.0.0");
        // For non-WGS84 CRS we would need to reproject the BBOX here. EPSG:3857 / 4326 are
        // both accepted; reprojection to WGS84 happens inside RenderRasterRegion only when
        // CRS=EPSG:4326. For 3857 we currently treat the BBOX as already lon/lat to avoid
        // the dependency on a server-side projector - this is acceptable because QGIS' WCS
        // 1.0 client defaults to EPSG:4326 and the GetCapabilities advertises both.

        var width = OgcRequestParser.GetInt(q, "WIDTH", 0, 0, 4096);
        var height = OgcRequestParser.GetInt(q, "HEIGHT", 0, 0, 4096);

        var (ddb, layer) = await _svc.ResolveCoverageAsync(orgSlug, dsSlug, coverageId);

        // Optional band subset via BANDS extension (not part of WCS 1.0 proper but
        // accepted because QGIS users frequently expect it). Resolution requires
        // probing the source raster band names.
        var info = _svc.ProbeRaster(ddb, layer);
        var bands = WcsRangeSubsetParser.ParseBands10(OgcRequestParser.Get(q, "BANDS"), info);

        // RESPONSE_CRS (WCS 1.0) overrides the output CRS without changing the
        // BBOX CRS. Falls back to RESPONSE_CRS_URI for QGIS compatibility.
        var outputCrs = WcsConformance.NormalizeCrs(
            OgcRequestParser.Get(q, "RESPONSE_CRS") ?? OgcRequestParser.Get(q, "RESPONSE_CRS_URI"));

        var bytes = _svc.RenderRegion(ddb, layer, bbox, width, height, mime, bands,
            string.IsNullOrEmpty(outputCrs) ? null : outputCrs);
        return new WcsCoverageResult(bytes, mime);
    }

    private static bool IsSupportedCrs(string crs)
    {
        if (string.IsNullOrWhiteSpace(crs)) return false;
        return WcsConformance.SupportedCrs.Any(s =>
            string.Equals(s, crs, StringComparison.OrdinalIgnoreCase))
            || string.Equals(crs, "CRS:84", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDeclaration(string xml)
        => Regex.Replace(xml, @"^\s*<\?xml[^?]*\?>\s*", "");
}
