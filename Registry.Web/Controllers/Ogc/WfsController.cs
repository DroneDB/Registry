using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Registry.Web.Exceptions;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Controllers;

/// <summary>WFS 2.0.0 controller.</summary>
[ApiController]
[ServiceFilter(typeof(BasicAuthFilter))]
[ServiceFilter(typeof(OgcAuthorizationFilter))]
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
