using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers;

/// <summary>WMTS 1.0.0 controller — KVP + RESTful tile retrieval.</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WmtsController : ControllerBaseEx
{
    private readonly IWmtsManager _mgr;
    private readonly ILogger<WmtsController> _logger;

    public WmtsController(IWmtsManager mgr, ILogger<WmtsController> logger)
    {
        _mgr = mgr; _logger = logger;
    }

    [HttpGet("wmts")]
    public async Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => await Dispatch(orgSlug, dsSlug, folderPath: null);

    [HttpGet("wmts/p/{*folder}")]
    public async Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => await Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            case "GETTILE":
                var layer = OgcRequestParser.GetRequired(q, "LAYER");
                var tms = OgcRequestParser.Get(q, "TILEMATRIXSET") ?? "GoogleMapsCompatible";
                var z = OgcRequestParser.GetInt(q, "TILEMATRIX", 0, 0, 24);
                var x = OgcRequestParser.GetInt(q, "TILECOL", 0, 0);
                var y = OgcRequestParser.GetInt(q, "TILEROW", 0, 0);
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";
                var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, tms, z, x, y, format);
                return File(bytes, format);
            default:
                throw new OgcException("OperationNotSupported",
                    $"WMTS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }

    [HttpGet("wmts/1.0.0/{layer}/{style}/{tms}/{z:int}/{y:int}/{x:int}.{ext}")]
    public async Task<IActionResult> Restful([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string layer, [FromRoute] string style, [FromRoute] string tms,
        [FromRoute] int z, [FromRoute] int y, [FromRoute] int x, [FromRoute] string ext)
    {
        var format = ext.ToLowerInvariant() switch
        {
            "pbf" => "application/vnd.mapbox-vector-tile",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
        // Route values may arrive percent-encoded when the layer identifier contains '/' (e.g. "folder%2Ffile.shp").
        layer = Uri.UnescapeDataString(layer);
        var bytes = await _mgr.GetTileAsync(orgSlug, dsSlug, layer, tms, z, x, y, format);
        return File(bytes, format);
    }
}

/// <summary>WMS 1.3.0 controller (with best-effort 1.1.1 negotiation).</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WmsController : ControllerBaseEx
{
    private readonly IWmsManager _mgr;
    public WmsController(IWmsManager mgr) { _mgr = mgr; }

    [HttpGet("wms")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wms/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        var rawVersion = OgcRequestParser.Get(q, "VERSION");
        var version = OgcRequestParser.NegotiateWmsVersion(rawVersion);
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, version, folderPath),
                    "text/xml; charset=utf-8");

            case "GETMAP":
            {
                if (string.IsNullOrWhiteSpace(rawVersion))
                    throw new OgcException("MissingParameterValue", "VERSION is required for GetMap", 400, "VERSION");
                var layers = OgcRequestParser.GetList(q, "LAYERS") ?? Array.Empty<string>();
                var styles = OgcRequestParser.GetList(q, "STYLES") ?? Array.Empty<string>();
                var crs = OgcRequestParser.Get(q, version == "1.3.0" ? "CRS" : "SRS") ?? "EPSG:4326";
                var bboxStr = OgcRequestParser.GetRequired(q, "BBOX");
                var (bbox, resolvedCrs) = OgcRequestParser.ParseBbox(bboxStr, crs, version);
                var width = OgcRequestParser.GetInt(q, "WIDTH", 256, 1, 4096);
                var height = OgcRequestParser.GetInt(q, "HEIGHT", 256, 1, 4096);
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";
                var bg = OgcRequestParser.Get(q, "BGCOLOR");
                var trans = string.Equals(OgcRequestParser.Get(q, "TRANSPARENT"), "TRUE",
                    StringComparison.OrdinalIgnoreCase);
                var bytes = await _mgr.GetMapAsync(orgSlug, dsSlug, layers, styles, bbox, resolvedCrs,
                    width, height, format, bg, trans);
                return File(bytes, format);
            }

            case "GETFEATUREINFO":
            {
                var queryLayers = OgcRequestParser.GetList(q, "QUERY_LAYERS")
                                  ?? OgcRequestParser.GetList(q, "LAYERS")
                                  ?? Array.Empty<string>();
                if (queryLayers.Length == 0)
                    throw new OgcException("MissingParameterValue", "QUERY_LAYERS required", 400, "QUERY_LAYERS");
                var crs = OgcRequestParser.Get(q, version == "1.3.0" ? "CRS" : "SRS") ?? "EPSG:4326";
                var bboxStr = OgcRequestParser.GetRequired(q, "BBOX");
                var (bbox, resolvedCrs) = OgcRequestParser.ParseBbox(bboxStr, crs, version);
                var width = OgcRequestParser.GetInt(q, "WIDTH", 256, 1, 4096);
                var height = OgcRequestParser.GetInt(q, "HEIGHT", 256, 1, 4096);
                var iKey = version == "1.3.0" ? "I" : "X";
                var jKey = version == "1.3.0" ? "J" : "Y";
                var iRaw = OgcRequestParser.GetRequired(q, iKey);
                var jRaw = OgcRequestParser.GetRequired(q, jKey);
                if (!int.TryParse(iRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i)
                    || i < 0 || i >= width)
                    throw new OgcException("InvalidPoint", $"{iKey} must be in [0,{width - 1}]", 400, iKey);
                if (!int.TryParse(jRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var j)
                    || j < 0 || j >= height)
                    throw new OgcException("InvalidPoint", $"{jKey} must be in [0,{height - 1}]", 400, jKey);
                var info = OgcRequestParser.Get(q, "INFO_FORMAT") ?? "application/json";
                var supportedInfo = new[] { "application/json", "text/xml", "application/xml", "text/html", "text/plain" };
                if (!supportedInfo.Any(f => string.Equals(f, info, StringComparison.OrdinalIgnoreCase)))
                    throw new OgcException("InvalidFormat", $"INFO_FORMAT '{info}' is not supported", 400, "INFO_FORMAT");
                var body = await _mgr.GetFeatureInfoAsync(orgSlug, dsSlug, queryLayers[0], bbox, resolvedCrs,
                    width, height, i, j, info);
                return Content(body, info);
            }

            default:
                throw new OgcException("OperationNotSupported",
                    $"WMS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}

/// <summary>WFS 2.0.0 controller.</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WfsController : ControllerBaseEx
{
    private readonly IWfsManager _mgr;
    public WfsController(IWfsManager mgr) { _mgr = mgr; }

    [HttpGet("wfs")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wfs/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    [HttpPost("wfs")]
    [Consumes("application/xml", "text/xml", "application/soap+xml")]
    public async Task<IActionResult> PostXml([FromRoute] string orgSlug, [FromRoute] string dsSlug)
    {
        using var reader = new StreamReader(Request.Body);
        var xml = await reader.ReadToEndAsync();
        return await DispatchPost(orgSlug, dsSlug, xml);
    }

    private async Task<IActionResult> DispatchPost(string orgSlug, string dsSlug, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new OgcException("MissingParameterValue", "Empty XML request body", 400);
        System.Xml.XmlDocument doc;
        try
        {
            doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
        }
        catch (Exception ex)
        {
            throw new OgcException("InvalidParameterValue", "Malformed XML: " + ex.Message, 400);
        }
        var root = doc.DocumentElement ?? throw new OgcException("InvalidParameterValue", "Empty XML", 400);
        var op = root.LocalName;
        const string nsWfs = "http://www.opengis.net/wfs/2.0";
        const string nsFes = "http://www.opengis.net/fes/2.0";

        if (op == "GetCapabilities")
            return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, null), "text/xml; charset=utf-8");

        if (op == "DescribeFeatureType")
        {
            var names = root.GetElementsByTagName("TypeName", nsWfs).Cast<System.Xml.XmlElement>()
                .Select(e => e.InnerText.Trim()).Where(s => s.Length > 0).ToArray();
            return Content(await _mgr.DescribeFeatureTypeAsync(orgSlug, dsSlug, names), "text/xml; charset=utf-8");
        }

        if (op == "ListStoredQueries")
            return Content(await _mgr.ListStoredQueriesAsync(orgSlug, dsSlug), "text/xml; charset=utf-8");

        if (op == "DescribeStoredQueries")
        {
            var ids = root.GetElementsByTagName("StoredQueryId", nsWfs).Cast<System.Xml.XmlElement>()
                .Select(e => e.InnerText.Trim()).Where(s => s.Length > 0).ToArray();
            return Content(await _mgr.DescribeStoredQueriesAsync(orgSlug, dsSlug, ids), "text/xml; charset=utf-8");
        }

        if (op == "GetFeature" || op == "GetPropertyValue")
        {
            var outputFormat = root.GetAttribute("outputFormat");
            if (string.IsNullOrWhiteSpace(outputFormat)) outputFormat = "application/gml+xml; version=3.2";
            var countAttr = root.GetAttribute("count");
            if (string.IsNullOrWhiteSpace(countAttr)) countAttr = root.GetAttribute("maxFeatures");
            var startIndexAttr = root.GetAttribute("startIndex");
            int count = int.TryParse(countAttr, out var c) ? c : 1000;
            int startIndex = int.TryParse(startIndexAttr, out var si) ? si : 0;

            // Stored query?
            var sqEls = root.GetElementsByTagName("StoredQuery", nsWfs);
            if (sqEls.Count > 0)
            {
                var sq = (System.Xml.XmlElement)sqEls[0]!;
                var qid = sq.GetAttribute("id");
                if (!string.Equals(qid, "urn:ogc:def:query:OGC-WFS::GetFeatureById", StringComparison.OrdinalIgnoreCase))
                    throw new OgcException("InvalidParameterValue", $"Unsupported stored query '{qid}'", 400, "StoredQuery");
                string? idVal = null;
                foreach (System.Xml.XmlElement p in sq.GetElementsByTagName("Parameter", nsWfs))
                {
                    if (string.Equals(p.GetAttribute("name"), "id", StringComparison.OrdinalIgnoreCase))
                        idVal = p.InnerText.Trim();
                }
                if (string.IsNullOrEmpty(idVal))
                    throw new OgcException("MissingParameterValue", "Parameter 'id' required", 400, "id");
                var b0 = await _mgr.GetFeatureByIdAsync(orgSlug, dsSlug, idVal, outputFormat);
                return Content(b0, outputFormat);
            }

            var queries = root.GetElementsByTagName("Query", nsWfs);
            if (queries.Count == 0)
                throw new OgcException("MissingParameterValue", "wfs:Query element required", 400);
            var query = (System.Xml.XmlElement)queries[0]!;
            var typeNamesAttr = query.GetAttribute("typeNames");
            if (string.IsNullOrWhiteSpace(typeNamesAttr)) typeNamesAttr = query.GetAttribute("typeName");
            var typeName = (typeNamesAttr ?? string.Empty).Split(' ', ',').FirstOrDefault()?.Trim() ?? "";
            if (string.IsNullOrEmpty(typeName))
                throw new OgcException("MissingParameterValue", "typeNames required", 400, "typeNames");

            var srsName = query.GetAttribute("srsName");
            if (string.IsNullOrWhiteSpace(srsName)) srsName = null;

            // Parse FES Filter
            string? resourceIdsCsv = null;
            string? filterXml = null;
            var filters = query.GetElementsByTagName("Filter", nsFes);
            if (filters.Count > 0)
            {
                var filter = (System.Xml.XmlElement)filters[0]!;
                var ridList = new List<string>();
                bool onlyResourceIds = true;
                foreach (System.Xml.XmlNode n in filter.ChildNodes)
                {
                    if (n is not System.Xml.XmlElement el) continue;
                    if (el.LocalName == "ResourceId" && el.NamespaceURI == nsFes)
                    {
                        var rid = el.GetAttribute("rid");
                        if (!string.IsNullOrEmpty(rid)) ridList.Add(rid);
                    }
                    else
                    {
                        onlyResourceIds = false;
                    }
                }
                if (onlyResourceIds && ridList.Count > 0)
                    resourceIdsCsv = string.Join(',', ridList);
                else
                    filterXml = filter.OuterXml;
            }

            // Validate CRS if specified
            if (!string.IsNullOrWhiteSpace(srsName))
            {
                var sn = srsName!.Trim();
                if (!(sn.Equals("EPSG:4326", StringComparison.OrdinalIgnoreCase)
                    || sn.Equals("urn:ogc:def:crs:EPSG::4326", StringComparison.OrdinalIgnoreCase)
                    || sn.Equals("http://www.opengis.net/def/crs/EPSG/0/4326", StringComparison.OrdinalIgnoreCase)
                    || sn.Equals("CRS:84", StringComparison.OrdinalIgnoreCase)
                    || sn.Equals("urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase)))
                    throw new OgcException("InvalidParameterValue", $"CRS '{sn}' not supported", 400, "srsName");
            }

            // Consistency check on ResourceId vs typeName for inconsistentFeatureIdentifierAndType test.
            // Cheap heuristic: rids commonly have form "<featureType>.<n>" where featureType is the
            // sanitized layer name. If prefix doesn't match the requested typeName's local part, 400.
            if (!string.IsNullOrEmpty(resourceIdsCsv))
            {
                var local = typeName.Contains(':') ? typeName.Substring(typeName.IndexOf(':') + 1) : typeName;
                var sanitized = System.Text.RegularExpressions.Regex.Replace(local, @"[^A-Za-z0-9_\-]", "_");
                foreach (var rid in resourceIdsCsv.Split(','))
                {
                    var dot = rid.LastIndexOf('.');
                    if (dot <= 0) continue;
                    var prefix = rid.Substring(0, dot);
                    if (!string.Equals(prefix, sanitized, StringComparison.Ordinal)
                        && !string.Equals(prefix, local, StringComparison.Ordinal))
                        throw new OgcException("InvalidParameterValue",
                            $"Resource id '{rid}' inconsistent with feature type '{typeName}'", 400, "ResourceId");
                }
            }

            var body = (op == "GetPropertyValue")
                ? await _mgr.GetPropertyValueAsync(orgSlug, dsSlug, typeName,
                    root.GetAttribute("valueReference"), count, startIndex, outputFormat,
                    resourceIdsCsv, filterXml)
                : await _mgr.GetFeatureAsync(orgSlug, dsSlug, typeName, null, srsName,
                    count, startIndex, outputFormat, resourceIdsCsv, filterXml);
            return Content(body, outputFormat);
        }

        throw new OgcException("OperationNotSupported", $"WFS operation '{op}' not supported", 400);
    }

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        // SERVICE param is required (OGC 06-121r3 §7.2.6). Enforced explicitly here to satisfy
        // the WFS 2.0 ETS getCapabilities_missingServiceParam test.
        if (string.Equals(request, "GetCapabilities", StringComparison.OrdinalIgnoreCase))
        {
            var svc = OgcRequestParser.Get(q, "SERVICE");
            if (string.IsNullOrWhiteSpace(svc))
                throw new OgcException("MissingParameterValue", "SERVICE parameter required", 400, "service");
            if (!string.Equals(svc, "WFS", StringComparison.OrdinalIgnoreCase))
                throw new OgcException("InvalidParameterValue",
                    $"SERVICE '{svc}' not supported by WFS endpoint", 400, "service");
        }
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            case "DESCRIBEFEATURETYPE":
            {
                var typeNames = OgcRequestParser.GetList(q, "TYPENAMES")
                                ?? OgcRequestParser.GetList(q, "TYPENAME")
                                ?? Array.Empty<string>();
                return Content(await _mgr.DescribeFeatureTypeAsync(orgSlug, dsSlug, typeNames),
                    "text/xml; charset=utf-8");
            }
            case "GETFEATURE":
            {
                var storedQueryId = OgcRequestParser.Get(q, "STOREDQUERY_ID");
                var format = OgcRequestParser.Get(q, "OUTPUTFORMAT") ?? "application/gml+xml; version=3.2";

                if (!string.IsNullOrWhiteSpace(storedQueryId))
                {
                    if (string.Equals(storedQueryId, "urn:ogc:def:query:OGC-WFS::GetFeatureById",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var idParam = OgcRequestParser.Get(q, "ID")
                                      ?? throw new OgcException("MissingParameterValue",
                                          "ID parameter required for GetFeatureById stored query", 400, "ID");
                        var body0 = await _mgr.GetFeatureByIdAsync(orgSlug, dsSlug, idParam, format);
                        return Content(body0, format);
                    }
                    throw new OgcException("InvalidParameterValue",
                        $"Unsupported STOREDQUERY_ID '{storedQueryId}'", 400, "STOREDQUERY_ID");
                }

                var typeName = (OgcRequestParser.Get(q, "TYPENAMES")
                                ?? OgcRequestParser.Get(q, "TYPENAME"))
                               ?? throw new OgcException("MissingParameterValue",
                                   "typeNames required", 400, "typeNames");
                var bbox = OgcRequestParser.Get(q, "BBOX");
                double[]? bboxArr = null;
                string? bboxCrs = null;
                if (!string.IsNullOrWhiteSpace(bbox))
                {
                    var parsed = OgcRequestParser.ParseBbox(bbox, OgcRequestParser.Get(q, "SRSNAME"), "2.0.0");
                    bboxArr = parsed.Bbox;
                    bboxCrs = parsed.Crs;
                }
                var count = OgcRequestParser.GetInt(q, "COUNT", 1000, 1, 10000);
                count = Math.Max(count, OgcRequestParser.GetInt(q, "MAXFEATURES", count, 1, 10000));
                var startIndex = OgcRequestParser.GetInt(q, "STARTINDEX", 0, 0);
                var resourceId = OgcRequestParser.Get(q, "RESOURCEID")
                                 ?? OgcRequestParser.Get(q, "FEATUREID");
                // FES FILTER param: raw FES Filter XML (URL-encoded by client). Pass through
                // to the manager which evaluates supported predicates against the FC.
                var filterXml = OgcRequestParser.Get(q, "FILTER");
                // Validate SRSNAME if provided (independent of BBOX).
                var srsParam = OgcRequestParser.Get(q, "SRSNAME");
                if (!string.IsNullOrWhiteSpace(srsParam) && bboxArr == null)
                {
                    var sn = srsParam!.Trim();
                    if (!(sn.Equals("EPSG:4326", StringComparison.OrdinalIgnoreCase)
                        || sn.Equals("urn:ogc:def:crs:EPSG::4326", StringComparison.OrdinalIgnoreCase)
                        || sn.Equals("http://www.opengis.net/def/crs/EPSG/0/4326", StringComparison.OrdinalIgnoreCase)
                        || sn.Equals("CRS:84", StringComparison.OrdinalIgnoreCase)
                        || sn.Equals("urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase)))
                        throw new OgcException("InvalidParameterValue",
                            $"CRS '{sn}' not supported", 400, "srsName");
                    bboxCrs ??= sn;
                }
                if (!string.IsNullOrEmpty(resourceId))
                {
                    var localT = typeName.Contains(':') ? typeName.Substring(typeName.IndexOf(':') + 1) : typeName;
                    var sanitizedT = System.Text.RegularExpressions.Regex.Replace(localT, @"[^A-Za-z0-9_\-]", "_");
                    foreach (var rid in resourceId.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var dot = rid.LastIndexOf('.');
                        if (dot <= 0) continue;
                        var prefix = rid.Substring(0, dot);
                        if (!string.Equals(prefix, sanitizedT, StringComparison.Ordinal)
                            && !string.Equals(prefix, localT, StringComparison.Ordinal))
                            throw new OgcException("InvalidParameterValue",
                                $"Resource id '{rid}' inconsistent with feature type '{typeName}'", 400, "RESOURCEID");
                    }
                }
                var body = await _mgr.GetFeatureAsync(orgSlug, dsSlug, typeName, bboxArr, bboxCrs,
                    count, startIndex, format, resourceId, filterXml);
                return Content(body, format);
            }
            case "GETPROPERTYVALUE":
            {
                var typeName = (OgcRequestParser.Get(q, "TYPENAMES")
                                ?? OgcRequestParser.Get(q, "TYPENAME"))
                               ?? throw new OgcException("MissingParameterValue",
                                   "typeNames required", 400, "typeNames");
                var valueRef = OgcRequestParser.Get(q, "VALUEREFERENCE")
                               ?? throw new OgcException("MissingParameterValue",
                                   "valueReference required", 400, "valueReference");
                var format = OgcRequestParser.Get(q, "OUTPUTFORMAT") ?? "application/gml+xml; version=3.2";
                var count = OgcRequestParser.GetInt(q, "COUNT", 1000, 1, 10000);
                var startIndex = OgcRequestParser.GetInt(q, "STARTINDEX", 0, 0);
                var resourceId = OgcRequestParser.Get(q, "RESOURCEID")
                                 ?? OgcRequestParser.Get(q, "FEATUREID");
                var filterXml = OgcRequestParser.Get(q, "FILTER");
                var body = await _mgr.GetPropertyValueAsync(orgSlug, dsSlug, typeName, valueRef,
                    count, startIndex, format, resourceId, filterXml);
                return Content(body, format);
            }
            case "LISTSTOREDQUERIES":
                return Content(await _mgr.ListStoredQueriesAsync(orgSlug, dsSlug),
                    "text/xml; charset=utf-8");
            case "DESCRIBESTOREDQUERIES":
            {
                var ids = OgcRequestParser.GetList(q, "STOREDQUERY_ID") ?? Array.Empty<string>();
                return Content(await _mgr.DescribeStoredQueriesAsync(orgSlug, dsSlug, ids),
                    "text/xml; charset=utf-8");
            }
            default:
                throw new OgcException("OperationNotSupported",
                    $"WFS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}

/// <summary>WCS 2.0 controller.</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug)]
public class WcsController : ControllerBaseEx
{
    private readonly IWcsManager _mgr;
    public WcsController(IWcsManager mgr) { _mgr = mgr; }

    [HttpGet("wcs")]
    public Task<IActionResult> Kvp([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Dispatch(orgSlug, dsSlug, null);

    [HttpGet("wcs/p/{*folder}")]
    public Task<IActionResult> KvpFolder([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string folder) => Dispatch(orgSlug, dsSlug, folder);

    private async Task<IActionResult> Dispatch(string orgSlug, string dsSlug, string? folderPath)
    {
        var q = Request.Query;
        var request = OgcRequestParser.GetRequired(q, "REQUEST");
        switch (request.ToUpperInvariant())
        {
            case "GETCAPABILITIES":
                return Content(await _mgr.GetCapabilitiesAsync(orgSlug, dsSlug, folderPath),
                    "text/xml; charset=utf-8");
            case "DESCRIBECOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");
                return Content(await _mgr.DescribeCoverageAsync(orgSlug, dsSlug, cid),
                    "text/xml; charset=utf-8");
            }
            case "GETCOVERAGE":
            {
                var cid = OgcRequestParser.GetRequired(q, "COVERAGEID");
                var format = OgcRequestParser.Get(q, "FORMAT") ?? "image/png";
                double[]? subset = null;
                var subsetParams = q
                    .Where(kv => string.Equals(kv.Key, "SUBSET", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kv => kv.Value.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
                    .ToArray();
                if (subsetParams.Length >= 2)
                {
                    // Best-effort minimal SUBSET parser: lat(minLat,maxLat),long(minLon,maxLon)
                    double? minLat = null, maxLat = null, minLon = null, maxLon = null;
                    foreach (var part in subsetParams)
                    {
                        var open = part.IndexOf('(');
                        var close = part.IndexOf(')');
                        if (open < 0 || close < 0) continue;
                        var axis = part[..open].Trim().ToLowerInvariant();
                        var range = part.Substring(open + 1, close - open - 1).Split(',');
                        if (range.Length != 2) continue;
                        if (!double.TryParse(range[0], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var lo) ||
                            !double.TryParse(range[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var hi)) continue;
                        if (axis.StartsWith("lat") || axis == "y") { minLat = lo; maxLat = hi; }
                        else if (axis.StartsWith("lon") || axis.StartsWith("long") || axis == "x")
                            { minLon = lo; maxLon = hi; }
                    }
                    if (minLon.HasValue && minLat.HasValue && maxLon.HasValue && maxLat.HasValue)
                        subset = new[] { minLon.Value, minLat.Value, maxLon.Value, maxLat.Value };
                }
                var bytes = await _mgr.GetCoverageAsync(orgSlug, dsSlug, cid, subset, format);
                return File(bytes, format);
            }
            default:
                throw new OgcException("OperationNotSupported",
                    $"WCS REQUEST '{request}' not supported", 400, "REQUEST");
        }
    }
}

/// <summary>OGC API – Features + Tiles controller (JSON REST).</summary>
[ApiController]
[Tags("OGC")]
[ServiceFilter(typeof(BasicAuthFilter))]
[TypeFilter(typeof(OgcExceptionFilter))]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" +
       RoutesHelper.DatasetRadix + "/" + RoutesHelper.DatasetSlug + "/ogcapi")]
public class OgcApiController : ControllerBaseEx
{
    private readonly IOgcApiFeaturesManager _features;
    private readonly IOgcApiTilesManager _tiles;

    public OgcApiController(IOgcApiFeaturesManager features, IOgcApiTilesManager tiles)
    {
        _features = features; _tiles = tiles;
    }

    private string BaseUrl([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => $"{Request.Scheme}://{Request.Host}/orgs/{orgSlug}/ds/{dsSlug}/ogcapi";

    [HttpGet("")]
    public async Task<IActionResult> Landing([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Ok(await _features.GetLandingAsync(orgSlug, dsSlug, BaseUrl(orgSlug, dsSlug)));

    [HttpGet("conformance")]
    public async Task<IActionResult> Conformance()
        => Ok(await _features.GetConformanceAsync());

    [HttpGet("collections")]
    public async Task<IActionResult> Collections([FromRoute] string orgSlug, [FromRoute] string dsSlug)
        => Ok(await _features.GetCollectionsAsync(orgSlug, dsSlug, BaseUrl(orgSlug, dsSlug)));

    [HttpGet("collections/{collectionId}")]
    public async Task<IActionResult> Collection([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId)
    {
        var col = await _features.GetCollectionAsync(orgSlug, dsSlug, collectionId, BaseUrl(orgSlug, dsSlug));
        return col == null ? NotFound() : Ok(col);
    }

    [HttpGet("collections/{collectionId}/items")]
    public async Task<IActionResult> Items([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId,
        [FromQuery] string? bbox = null,
        [FromQuery] int limit = 10,
        [FromQuery] int offset = 0)
    {
        double[]? bboxArr = null;
        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parsed = OgcRequestParser.ParseBbox(bbox, "CRS:84", null);
            bboxArr = parsed.Bbox;
        }
        var json = await _features.GetItemsAsync(orgSlug, dsSlug, collectionId, bboxArr, limit, offset);
        return Content(json, "application/geo+json");
    }

    [HttpGet("collections/{collectionId}/items/{featureId}")]
    public async Task<IActionResult> Item([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, [FromRoute] string featureId)
    {
        var json = await _features.GetItemAsync(orgSlug, dsSlug, collectionId, featureId);
        return Content(json, "application/geo+json");
    }

    [HttpGet("collections/{collectionId}/tiles")]
    public async Task<IActionResult> TileSets([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId)
        => Ok(await _tiles.GetTileSetsAsync(orgSlug, dsSlug, collectionId, BaseUrl(orgSlug, dsSlug)));

    [HttpGet("collections/{collectionId}/tiles/{tileMatrixSet}/{z:int}/{y:int}/{x:int}")]
    public async Task<IActionResult> Tile([FromRoute] string orgSlug, [FromRoute] string dsSlug,
        [FromRoute] string collectionId, [FromRoute] string tileMatrixSet,
        [FromRoute] int z, [FromRoute] int y, [FromRoute] int x)
    {
        var bytes = await _tiles.GetTileAsync(orgSlug, dsSlug, collectionId, tileMatrixSet, z, x, y);
        if (bytes == null) return NotFound();
        return File(bytes, "application/octet-stream");
    }
}
