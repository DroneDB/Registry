using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using Registry.Web.Services.Caching;

namespace Registry.Web.Services.Managers.Wcs;

/// <summary>
/// WCS 1.1.1 protocol handler (OGC 07-067r5). Uses OWS Common 1.1 for envelope/ServiceIdentification
/// and the WCS 1.1 grid model (GridBaseCRS / GridOrigin / GridOffsets). GetCoverage returns raw bytes
/// in the requested MIME (we deliberately skip the multipart/related envelope: when one coverage is
/// requested a single image stream is the most interoperable answer for QGIS and gdalwcs.)
/// </summary>
public sealed class WcsProtocol11Handler : IWcsProtocolHandler
{
    public string Version => "1.1.1";

    private readonly IWcsCoverageService _svc;
    private readonly IRecordCache _records;

    private const string NsWcs = "http://www.opengis.net/wcs/1.1";
    private const string NsOws = "http://www.opengis.net/ows/1.1";
    private const string NsXlink = "http://www.w3.org/1999/xlink";
    private const string Utf8XmlDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
    private static readonly RecordCacheOptions CapabilitiesCache = RecordCacheOptions.For(TimeSpan.FromMinutes(5));

    public WcsProtocol11Handler(IWcsCoverageService svc, IRecordCache records)
    {
        _svc = svc;
        _records = records;
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath,
        CancellationToken ct = default)
    {
        var key = await _svc.CacheKeyAsync(orgSlug, dsSlug, "caps-wcs11-v1", folderPath, ct);
        return await _records.GetOrCreateRecordAsync(key, async token =>
        {

            var (_, layers) = await _svc.GetCoveragesAsync(orgSlug, dsSlug, folderPath, token);
            var baseUrl = _svc.GetBaseUrl(orgSlug, dsSlug, folderPath);

            var sb = new StringBuilder();
            await using (var w = XmlWriter.Create(sb,
                new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true }))
            {
                await w.WriteStartElementAsync("wcs", "Capabilities", NsWcs);
                await w.WriteAttributeStringAsync("xmlns", "ows", null, NsOws);
                await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
                w.WriteAttributeString("version", "1.1.1");

                // ows:ServiceIdentification
                await w.WriteStartElementAsync("ows", "ServiceIdentification", NsOws);
                await w.WriteElementStringAsync("ows", "Title", NsOws, $"DroneDB WCS - {orgSlug}/{dsSlug}");
                await w.WriteElementStringAsync("ows", "ServiceType", NsOws, "WCS");
                await w.WriteElementStringAsync("ows", "ServiceTypeVersion", NsOws, "1.1.1");
                await w.WriteElementStringAsync("ows", "ServiceTypeVersion", NsOws, "1.1.0");
                await w.WriteEndElementAsync();

                // ows:OperationsMetadata
                await w.WriteStartElementAsync("ows", "OperationsMetadata", NsOws);
                foreach (var op in WcsConformance.Operations)
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

                // wcs:Contents
                await w.WriteStartElementAsync("wcs", "Contents", null);
                foreach (var l in layers)
                {
                    await w.WriteStartElementAsync("wcs", "CoverageSummary", null);
                    await w.WriteElementStringAsync("ows", "Title", NsOws,
                        string.IsNullOrEmpty(l.Title) ? l.Name : l.Title);
                    if (l.BboxWgs84 != null)
                    {
                        await w.WriteStartElementAsync("ows", "WGS84BoundingBox", NsOws);
                        await w.WriteElementStringAsync("ows", "LowerCorner", NsOws,
                            FormattableString.Invariant($"{l.BboxWgs84[0]} {l.BboxWgs84[1]}"));
                        await w.WriteElementStringAsync("ows", "UpperCorner", NsOws,
                            FormattableString.Invariant($"{l.BboxWgs84[2]} {l.BboxWgs84[3]}"));
                        await w.WriteEndElementAsync();
                    }
                    await w.WriteElementStringAsync("wcs", "Identifier", null, OgcNames.ToNcName(l.Name));
                    foreach (var f in WcsConformance.SupportedFormats)
                        await w.WriteElementStringAsync("wcs", "SupportedFormat", null, f);
                    await w.WriteEndElementAsync();
                }
                await w.WriteEndElementAsync();

                await w.WriteEndElementAsync(); // wcs:Capabilities
                await w.WriteEndDocumentAsync();
                await w.FlushAsync();
            }
            var xml = Utf8XmlDecl + StripDeclaration(sb.ToString());
            return xml;
        }, CapabilitiesCache, ct);
    }

    public async Task<string> DescribeCoverageAsync(string orgSlug, string dsSlug, string coverageIds,
        CancellationToken ct = default)
    {
        var ids = (coverageIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var (ddb, resolved) = await _svc.ResolveCoveragesAsync(orgSlug, dsSlug, ids, ct);

        var sb = new StringBuilder();
        await using (var w = XmlWriter.Create(sb,
            new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true }))
        {
            await w.WriteStartElementAsync("wcs", "CoverageDescriptions", NsWcs);
            await w.WriteAttributeStringAsync("xmlns", "ows", null, NsOws);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);

            foreach (var (id, layer) in resolved)
                await WriteCoverageDescriptionAsync(w, ddb, id, layer, ct);

            await w.WriteEndElementAsync();
            await w.WriteEndDocumentAsync();
            await w.FlushAsync();
        }
        return Utf8XmlDecl + StripDeclaration(sb.ToString());
    }

    private async Task WriteCoverageDescriptionAsync(XmlWriter w, IDDB ddb, string id, OgcLayerDto layer,
        CancellationToken ct)
    {
        var bbox = layer.BboxWgs84 ?? [-180.0, -90.0, 180.0, 90.0];
        var info = await _svc.ProbeRasterAsync(ddb, layer, ct);
        var rWidth = info.Width > 0 ? info.Width : 1024;
        var rHeight = info.Height > 0 ? info.Height : 1024;

        await w.WriteStartElementAsync("wcs", "CoverageDescription", null);
        await w.WriteElementStringAsync("ows", "Title", NsOws,
            string.IsNullOrEmpty(layer.Title) ? layer.Name : layer.Title);
        await w.WriteElementStringAsync("wcs", "Identifier", null, OgcNames.ToNcName(id));

        // Domain
        await w.WriteStartElementAsync("wcs", "Domain", null);
        await w.WriteStartElementAsync("wcs", "SpatialDomain", null);
        await w.WriteStartElementAsync("ows", "BoundingBox", NsOws);
        w.WriteAttributeString("crs", "urn:ogc:def:crs:EPSG::4326");
        w.WriteAttributeString("dimensions", "2");
        await w.WriteElementStringAsync("ows", "LowerCorner", NsOws,
            FormattableString.Invariant($"{bbox[0]} {bbox[1]}"));
        await w.WriteElementStringAsync("ows", "UpperCorner", NsOws,
            FormattableString.Invariant($"{bbox[2]} {bbox[3]}"));
        await w.WriteEndElementAsync();

        // GridCRS
        var dx = (bbox[2] - bbox[0]) / Math.Max(1, rWidth);
        var dy = (bbox[3] - bbox[1]) / Math.Max(1, rHeight);
        await w.WriteStartElementAsync("wcs", "GridCRS", null);
        await w.WriteElementStringAsync("wcs", "GridBaseCRS", null, "urn:ogc:def:crs:EPSG::4326");
        await w.WriteElementStringAsync("wcs", "GridType", null,
            "urn:ogc:def:method:WCS:1.1:2dGridIn2dCrs");
        await w.WriteElementStringAsync("wcs", "GridOrigin", null,
            FormattableString.Invariant($"{bbox[0]} {bbox[3]}"));
        await w.WriteElementStringAsync("wcs", "GridOffsets", null,
            FormattableString.Invariant($"{dx} {-dy}"));
        await w.WriteElementStringAsync("wcs", "GridCS", null,
            "urn:ogc:def:cs:OGC:0.0:Grid2dSquareCS");
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync(); // SpatialDomain
        await w.WriteEndElementAsync(); // Domain

        // Range
        await w.WriteStartElementAsync("wcs", "Range", null);
        await w.WriteStartElementAsync("wcs", "Field", null);
        await w.WriteElementStringAsync("wcs", "Identifier", null, "bands");
        await w.WriteStartElementAsync("wcs", "Definition", null);
        await w.WriteStartElementAsync("ows", "AnyValue", NsOws);
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteStartElementAsync("wcs", "InterpolationMethods", null);
        await w.WriteElementStringAsync("wcs", "InterpolationMethod", null, "nearest");
        await w.WriteElementStringAsync("wcs", "Default", null, "nearest");
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync(); // Field
        await w.WriteEndElementAsync(); // Range

        // SupportedCRS / SupportedFormat
        foreach (var crs in WcsConformance.SupportedCrs)
            await w.WriteElementStringAsync("wcs", "SupportedCRS", null, $"urn:ogc:def:crs:{crs.Replace(":", "::")}");
        foreach (var f in WcsConformance.SupportedFormats)
            await w.WriteElementStringAsync("wcs", "SupportedFormat", null, f);

        await w.WriteEndElementAsync(); // CoverageDescription
    }

    public async Task<WcsCoverageResult> GetCoverageAsync(string orgSlug, string dsSlug, IQueryCollection q,
        CancellationToken ct = default)
    {
        var coverageId = OgcRequestParser.GetRequired(q, "Identifier");
        var format = OgcRequestParser.GetRequired(q, "FORMAT");
        if (!WcsConformance.SupportedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
            throw new OgcException("InvalidParameterValue",
                $"format '{format}' is not supported", 400, "format");

        // WCS 1.1: BoundingBox=minx,miny,maxx,maxy,<crsUri>
        var raw = OgcRequestParser.GetRequired(q, "BoundingBox");
        var (bbox, _) = OgcRequestParser.ParseBbox(raw, null, "1.1.1");

        // Honour GridOffsets when present to derive WIDTH/HEIGHT; otherwise auto-size.
        var gridOffsets = OgcRequestParser.Get(q, "GridOffsets");
        int width = 0, height = 0;
        if (!string.IsNullOrWhiteSpace(gridOffsets))
        {
            var parts = gridOffsets.Split([',', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dx)
                && double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dyRaw))
            {
                var dy = Math.Abs(dyRaw);
                if (dx > 0) width = (int)Math.Round((bbox[2] - bbox[0]) / dx);
                if (dy > 0) height = (int)Math.Round((bbox[3] - bbox[1]) / dy);
            }
        }

        var (ddb, layer) = await _svc.ResolveCoverageAsync(orgSlug, dsSlug, coverageId, ct);

        // OGC 07-067r5 §9.3.2.4 - optional band subset via RangeSubset.
        var info = await _svc.ProbeRasterAsync(ddb, layer, ct);
        var bands = WcsRangeSubsetParser.ParseRangeSubset11(
            OgcRequestParser.Get(q, "RangeSubset"), info);

        var bytes = await _svc.RenderRegionAsync(ddb, layer, bbox, width, height, format, bands, ct: ct);
        return new WcsCoverageResult(bytes, format);
    }

    private static string StripDeclaration(string xml)
        => Regex.Replace(xml, @"^\s*<\?xml[^?]*\?>\s*", "");
}
