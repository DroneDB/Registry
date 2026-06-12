using System;
using System.Collections.Generic;
using System.Globalization;
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
/// WCS 2.0.1 protocol handler. Implements the wire format mandated by OGC 09-110r4
/// (Core + GET KVP binding): GMLCOV-based CoverageDescription, ows:* namespaces,
/// <c>SUBSET=Long(...),Lat(...)</c> selection, FORMAT validated against
/// <see cref="WcsConformance.SupportedFormats"/>.
/// </summary>
public sealed class WcsProtocol20Handler : IWcsProtocolHandler
{
    public string Version => "2.0.1";

    private readonly IWcsCoverageService _svc;
    private readonly IDistributedCache _cache;

    private const string NsWcs = "http://www.opengis.net/wcs/2.0";
    private const string NsOws = "http://www.opengis.net/ows/2.0";
    private const string NsGml = "http://www.opengis.net/gml/3.2";
    private const string NsGmlcov = "http://www.opengis.net/gmlcov/1.0";
    private const string NsSwe = "http://www.opengis.net/swe/2.0";
    private const string NsXlink = "http://www.w3.org/1999/xlink";
    private const string Utf8XmlDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public WcsProtocol20Handler(IWcsCoverageService svc, IDistributedCache cache)
    {
        _svc = svc;
        _cache = cache;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath)
    {
        var key = $"ogc-caps-wcs20-v1-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await _cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        var (_, layers) = await _svc.GetCoveragesAsync(orgSlug, dsSlug, folderPath);
        var baseUrl = _svc.GetBaseUrl(orgSlug, dsSlug, folderPath);

        var sb = new StringBuilder();
        await using (var w = XmlWriter.Create(sb,
            new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true }))
        {
            await w.WriteStartElementAsync("wcs", "Capabilities", NsWcs);
            await w.WriteAttributeStringAsync("xmlns", "ows", null, NsOws);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
            w.WriteAttributeString("version", "2.0.1");

            await w.WriteStartElementAsync("ows", "ServiceIdentification", NsOws);
            await w.WriteElementStringAsync("ows", "Title", NsOws, $"DroneDB WCS - {orgSlug}/{dsSlug}");
            await w.WriteElementStringAsync("ows", "ServiceType", NsOws, "OGC WCS");
            foreach (var v in WcsConformance.SupportedVersions)
                await w.WriteElementStringAsync("ows", "ServiceTypeVersion", NsOws, v);
            foreach (var profile in WcsConformance.Profiles)
                await w.WriteElementStringAsync("ows", "Profile", NsOws, profile);
            await w.WriteEndElementAsync();

            await w.WriteStartElementAsync("ows", "OperationsMetadata", NsOws);
            foreach (var op in new[] { "GetCapabilities", "DescribeCoverage", "GetCoverage" })
            {
                await w.WriteStartElementAsync("ows", "Operation", NsOws);
                w.WriteAttributeString("name", op);
                await w.WriteStartElementAsync("ows", "DCP", NsOws);
                await w.WriteStartElementAsync("ows", "HTTP", NsOws);
                await w.WriteStartElementAsync("ows", "Get", NsOws);
                await w.WriteAttributeStringAsync("xlink", "href", NsXlink, baseUrl + "?");
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();

            await w.WriteStartElementAsync("wcs", "ServiceMetadata", NsWcs);
            foreach (var f in WcsConformance.SupportedFormats)
                await w.WriteElementStringAsync("wcs", "formatSupported", NsWcs, f);
            await w.WriteEndElementAsync();

            await w.WriteStartElementAsync("wcs", "Contents", null);
            foreach (var l in layers)
            {
                await w.WriteStartElementAsync("wcs", "CoverageSummary", null);
                await w.WriteElementStringAsync("wcs", "CoverageId", null, OgcNames.ToNcName(l.Name));
                await w.WriteElementStringAsync("wcs", "CoverageSubtype", null, "RectifiedGridCoverage");
                if (l.BboxWgs84 != null)
                {
                    await w.WriteStartElementAsync("ows", "WGS84BoundingBox", NsOws);
                    await w.WriteElementStringAsync("ows", "LowerCorner", NsOws,
                        FormattableString.Invariant($"{l.BboxWgs84[0]} {l.BboxWgs84[1]}"));
                    await w.WriteElementStringAsync("ows", "UpperCorner", NsOws,
                        FormattableString.Invariant($"{l.BboxWgs84[2]} {l.BboxWgs84[3]}"));
                    await w.WriteEndElementAsync();
                }
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndDocumentAsync();
            await w.FlushAsync();
        }
        var xml = Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
        await _cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    public async Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string coverageIds)
    {
        var ids = (coverageIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var (ddb, resolved) = await _svc.ResolveCoveragesAsync(orgSlug, dsSlug, ids);

        var sb = new StringBuilder();
        await using (var w = XmlWriter.Create(sb,
            new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true }))
        {
            await w.WriteStartElementAsync("wcs", "CoverageDescriptions", NsWcs);
            await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
            await w.WriteAttributeStringAsync("xmlns", "ows", null, NsOws);
            await w.WriteAttributeStringAsync("xmlns", "gmlcov", null, NsGmlcov);
            await w.WriteAttributeStringAsync("xmlns", "swe", null, NsSwe);

            foreach (var (id, layer) in resolved)
                await WriteCoverageDescriptionAsync(w, ddb, id, layer);

            await w.WriteEndElementAsync();
            await w.WriteEndDocumentAsync();
            await w.FlushAsync();
        }
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }

    private async Task WriteCoverageDescriptionAsync(XmlWriter w, IDDB ddb, string id, OgcLayerDto layer)
    {
        var gmlId = OgcNames.ToNcName(id);
        var info = _svc.ProbeRaster(ddb, layer);
        var rWidth = info.Width > 0 ? info.Width : 1024;
        var rHeight = info.Height > 0 ? info.Height : 1024;
        var rBands = info.BandCount > 0 ? info.BandCount : Math.Max(1, layer.BandCount);
        var bandNames = new List<string>(info.BandNames);
        while (bandNames.Count < rBands) bandNames.Add($"band{bandNames.Count + 1}");

        await w.WriteStartElementAsync("wcs", "CoverageDescription", null);
        await w.WriteAttributeStringAsync("gml", "id", NsGml, gmlId);

        const string srs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";
        var bbox = layer.BboxWgs84 ?? [-180.0, -90.0, 180.0, 90.0];
        await w.WriteStartElementAsync("gml", "boundedBy", NsGml);
        await w.WriteStartElementAsync("gml", "Envelope", NsGml);
        w.WriteAttributeString("srsName", srs);
        w.WriteAttributeString("axisLabels", "Long Lat");
        w.WriteAttributeString("uomLabels", "deg deg");
        w.WriteAttributeString("srsDimension", "2");
        await w.WriteElementStringAsync("gml", "lowerCorner", NsGml,
            FormattableString.Invariant($"{bbox[0]} {bbox[1]}"));
        await w.WriteElementStringAsync("gml", "upperCorner", NsGml,
            FormattableString.Invariant($"{bbox[2]} {bbox[3]}"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();

        await w.WriteElementStringAsync("wcs", "CoverageId", null, gmlId);

        await w.WriteStartElementAsync("gml", "domainSet", NsGml);
        await w.WriteStartElementAsync("gml", "RectifiedGrid", NsGml);
        await w.WriteAttributeStringAsync("gml", "id", NsGml, gmlId + "_grid");
        w.WriteAttributeString("dimension", "2");
        await w.WriteStartElementAsync("gml", "limits", NsGml);
        await w.WriteStartElementAsync("gml", "GridEnvelope", NsGml);
        await w.WriteElementStringAsync("gml", "low", NsGml, "0 0");
        await w.WriteElementStringAsync("gml", "high", NsGml,
            FormattableString.Invariant($"{rWidth - 1} {rHeight - 1}"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteElementStringAsync("gml", "axisLabels", NsGml, "Long Lat");
        await w.WriteStartElementAsync("gml", "origin", NsGml);
        await w.WriteStartElementAsync("gml", "Point", NsGml);
        await w.WriteAttributeStringAsync("gml", "id", NsGml, gmlId + "_origin");
        w.WriteAttributeString("srsName", srs);
        await w.WriteElementStringAsync("gml", "pos", NsGml,
            FormattableString.Invariant($"{bbox[0]} {bbox[3]}"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        var dx = (bbox[2] - bbox[0]) / Math.Max(1, rWidth);
        var dy = (bbox[3] - bbox[1]) / Math.Max(1, rHeight);
        await w.WriteStartElementAsync("gml", "offsetVector", NsGml);
        w.WriteAttributeString("srsName", srs);
        await w.WriteStringAsync(FormattableString.Invariant($"{dx} 0"));
        await w.WriteEndElementAsync();
        await w.WriteStartElementAsync("gml", "offsetVector", NsGml);
        w.WriteAttributeString("srsName", srs);
        await w.WriteStringAsync(FormattableString.Invariant($"0 {-dy}"));
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync("gmlcov", "rangeType", NsGmlcov);
        await w.WriteStartElementAsync("swe", "DataRecord", NsSwe);
        for (var i = 0; i < rBands; i++)
        {
            var bn = OgcNames.ToNcName(bandNames[i]);
            await w.WriteStartElementAsync("swe", "field", NsSwe);
            w.WriteAttributeString("name", bn);
            await w.WriteStartElementAsync("swe", "Quantity", NsSwe);
            await w.WriteStartElementAsync("swe", "uom", NsSwe);
            w.WriteAttributeString("code", "10^0");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();

        await w.WriteStartElementAsync("wcs", "ServiceParameters", null);
        await w.WriteElementStringAsync("wcs", "CoverageSubtype", null, "RectifiedGridCoverage");
        await w.WriteElementStringAsync("wcs", "nativeFormat", null, "image/tiff");
        await w.WriteEndElementAsync();

        await w.WriteEndElementAsync();
    }

    public async Task<WcsCoverageResult> GetCoverageAsync(string orgSlug, string dsSlug, IQueryCollection q)
    {
        var coverageId = OgcRequestParser.GetRequired(q, "COVERAGEID");

        // WCS 2.0 Req29: mediaType, when present, must equal multipart/related.
        var mediaType = OgcRequestParser.Get(q, "MEDIATYPE");
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            !string.Equals(mediaType, "multipart/related", StringComparison.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"mediaType '{mediaType}' is invalid; only 'multipart/related' is allowed (WCS 2.0 Req29)",
                400, "mediaType");

        // WCS 2.0 Req32/33: FORMAT optional → native (image/tiff); when present must be supported.
        var format = OgcRequestParser.Get(q, "FORMAT");
        if (string.IsNullOrWhiteSpace(format)) format = "image/tiff";
        else if (!WcsConformance.SupportedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"format '{format}' is not supported", 400, "format");

        // Multiple SUBSET KVPs come as a single key with a StringValues collection. Iterate
        // each entry individually (joining via ToString() would corrupt parenthesised values
        // by interleaving them with ',' separators). Also accept ';'-joined values.
        var subsetParams = q
            .Where(kv => string.Equals(kv.Key, "SUBSET", StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value.AsEnumerable())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v!.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        var (ddb, layer) = await _svc.ResolveCoverageAsync(orgSlug, dsSlug, coverageId);
        var coverageBbox = layer.BboxWgs84
            ?? throw new OgcException("InvalidParameterValue", "Coverage has no BBOX", 400);
        var bbox = WcsSubsetParser.Parse(subsetParams, coverageBbox) ?? coverageBbox;

        // OGC 09-147r3 §8.3 - optional band subset and output CRS overrides.
        var info = _svc.ProbeRaster(ddb, layer);
        var bands = WcsRangeSubsetParser.ParseRangeSubset20(
            OgcRequestParser.Get(q, "RANGESUBSET"), info);
        var outputCrs = WcsConformance.NormalizeCrs(OgcRequestParser.Get(q, "OUTPUTCRS"));

        var bytes = _svc.RenderRegion(ddb, layer, bbox, 0, 0, format, bands,
            string.IsNullOrEmpty(outputCrs) ? null : outputCrs);
        return new WcsCoverageResult(bytes, format);
    }
}
