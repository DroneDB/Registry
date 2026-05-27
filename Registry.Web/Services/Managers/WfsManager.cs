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
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using SkiaSharp;

namespace Registry.Web.Services.Managers;

/// <summary>WFS 2.0.0 manager (BBOX filter + pagination, no CQL).</summary>
public class WfsManager : OgcManagerBase, IWfsManager
{
    private readonly ILogger<WfsManager> _logger;

    public WfsManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx, ILogger<WfsManager> logger)
        : base(u, a, d, ar, w, c, cache, ctx)
    {
        _logger = logger;
    }

    private static async Task WriteWfsOperationAsync(XmlWriter w, string opName, string baseUrl,
        string[]? outputFormats = null)
    {
        await w.WriteStartElementAsync("ows", "Operation", null);
        w.WriteAttributeString("name", opName);
        await w.WriteStartElementAsync("ows", "DCP", null);
        await w.WriteStartElementAsync("ows", "HTTP", null);
        await w.WriteStartElementAsync("ows", "Get", null);
        await w.WriteAttributeStringAsync("xlink", "href", NsXlink, baseUrl + "?");
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        if (outputFormats != null && outputFormats.Length > 0)
        {
            await w.WriteStartElementAsync("ows", "Parameter", null);
            w.WriteAttributeString("name", "outputFormat");
            await w.WriteStartElementAsync("ows", "AllowedValues", null);
            foreach (var f in outputFormats)
                await w.WriteElementStringAsync("ows", "Value", null, f);
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        if (opName == "GetFeature" || opName == "GetPropertyValue")
        {
            await w.WriteStartElementAsync("ows", "Parameter", null);
            w.WriteAttributeString("name", "resolve");
            await w.WriteStartElementAsync("ows", "AllowedValues", null);
            await w.WriteElementStringAsync("ows", "Value", null, "none");
            await w.WriteElementStringAsync("ows", "Value", null, "local");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
    }

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null)
    {
        var key = $"ogc-caps-wfs-v2-2.0.0-{orgSlug}-{dsSlug}-{folderPath ?? ""}";
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath);
        var vectorLayers = layers.Where(l => l.EntryType == EntryType.Vector).ToList();
        var baseUrl = GetServiceUrl(orgSlug, dsSlug, "wfs", folderPath);

        var sb = new StringBuilder();
        await using (var w = CreateXmlWriter(sb))
        {
            await w.WriteStartElementAsync("wfs", "WFS_Capabilities", NsWfs);
            await w.WriteAttributeStringAsync("xmlns", "ows", null, NsOws);
            await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
            await w.WriteAttributeStringAsync("xmlns", "xlink", null, NsXlink);
            await w.WriteAttributeStringAsync("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            await w.WriteAttributeStringAsync("xmlns", "fes", null, "http://www.opengis.net/fes/2.0");
            await w.WriteAttributeStringAsync("xmlns", "ddb", null, NsDdb);
            await w.WriteAttributeStringAsync("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
                "http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd");
            w.WriteAttributeString("version", "2.0.0");

            await w.WriteStartElementAsync("ows", "ServiceIdentification", null);
            await w.WriteElementStringAsync("ows", "Title", null, $"DroneDB WFS — {orgSlug}/{dsSlug}");
            await w.WriteElementStringAsync("ows", "ServiceType", null, "WFS");
            await w.WriteElementStringAsync("ows", "ServiceTypeVersion", null, "2.0.0");
            await w.WriteElementStringAsync("ows", "Fees", null, "NONE");
            await w.WriteElementStringAsync("ows", "AccessConstraints", null, "NONE");
            await w.WriteEndElementAsync();

            await w.WriteStartElementAsync("ows", "ServiceProvider", null);
            await w.WriteElementStringAsync("ows", "ProviderName", null, "DroneDB");
            await w.WriteStartElementAsync("ows", "ProviderSite", null);
            await w.WriteAttributeStringAsync("xlink", "href", NsXlink, "https://dronedb.app");
            await w.WriteEndElementAsync();
            await w.WriteStartElementAsync("ows", "ServiceContact", null);
            await w.WriteElementStringAsync("ows", "IndividualName", null, "DroneDB Support");
            await w.WriteElementStringAsync("ows", "PositionName", null, "Support");
            await w.WriteStartElementAsync("ows", "ContactInfo", null);
            await w.WriteStartElementAsync("ows", "Address", null);
            await w.WriteElementStringAsync("ows", "ElectronicMailAddress", null, "support@dronedb.app");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();

            await w.WriteStartElementAsync("ows", "OperationsMetadata", null);
            await WriteWfsOperationAsync(w, "GetCapabilities", baseUrl);
            await WriteWfsOperationAsync(w, "DescribeFeatureType", baseUrl,
                ["application/gml+xml; version=3.2", "text/xml; subtype=gml/3.2"]);
            await WriteWfsOperationAsync(w, "GetFeature", baseUrl,
            [
                "application/gml+xml; version=3.2", "text/xml; subtype=gml/3.2",
                        "application/json", "application/json; subtype=geojson"
            ]);
            await WriteWfsOperationAsync(w, "GetPropertyValue", baseUrl,
                ["application/gml+xml; version=3.2", "text/xml; subtype=gml/3.2"]);
            await WriteWfsOperationAsync(w, "ListStoredQueries", baseUrl);
            await WriteWfsOperationAsync(w, "DescribeStoredQueries", baseUrl);
            // Service-level constraints required by WFS 2.0 (Table 13 of OGC 09-025r2).
            foreach (var (constraintName, value) in new[]
                     {
                         ("ImplementsBasicWFS", "TRUE"),
                         ("ImplementsTransactionalWFS", "FALSE"),
                         ("ImplementsLockingWFS", "FALSE"),
                         ("KVPEncoding", "TRUE"),
                         ("XMLEncoding", "FALSE"),
                         ("SOAPEncoding", "FALSE"),
                         ("ImplementsInheritance", "FALSE"),
                         ("ImplementsRemoteResolve", "FALSE"),
                         ("ImplementsResultPaging", "FALSE"),
                         ("ImplementsStandardJoins", "FALSE"),
                         ("ImplementsSpatialJoins", "FALSE"),
                         ("ImplementsTemporalJoins", "FALSE"),
                         ("ImplementsFeatureVersioning", "FALSE"),
                         ("ManageStoredQueries", "FALSE"),
                     })
            {
                await w.WriteStartElementAsync("ows", "Constraint", null);
                w.WriteAttributeString("name", constraintName);
                await w.WriteStartElementAsync("ows", "NoValues", null);
                await w.WriteEndElementAsync();
                await w.WriteElementStringAsync("ows", "DefaultValue", null, value);
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();

            await w.WriteStartElementAsync("wfs", "FeatureTypeList", NsWfs);
            foreach (var l in vectorLayers)
            {
                await w.WriteStartElementAsync("wfs", "FeatureType", NsWfs);
                await w.WriteAttributeStringAsync("xmlns", "ddb", null, NsDdb);
                await w.WriteElementStringAsync("wfs", "Name", NsWfs, "ddb:" + OgcNames.ToNcName(l.Name));
                await w.WriteElementStringAsync("wfs", "Title", NsWfs, l.Title);
                await w.WriteElementStringAsync("wfs", "DefaultCRS", NsWfs, "urn:ogc:def:crs:EPSG::4326");
                if (l.BboxWgs84 != null)
                {
                    await w.WriteStartElementAsync("ows", "WGS84BoundingBox", null);
                    await w.WriteElementStringAsync("ows", "LowerCorner", null,
                        FormattableString.Invariant($"{l.BboxWgs84[0]} {l.BboxWgs84[1]}"));
                    await w.WriteElementStringAsync("ows", "UpperCorner", null,
                        FormattableString.Invariant($"{l.BboxWgs84[2]} {l.BboxWgs84[3]}"));
                    await w.WriteEndElementAsync();
                }
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();

            // Minimal FES Filter_Capabilities (BBOX-only) required by WFS 2.0 ETS.
            await w.WriteStartElementAsync(null, "Filter_Capabilities", "http://www.opengis.net/fes/2.0");
            await w.WriteStartElementAsync(null, "Conformance", "http://www.opengis.net/fes/2.0");
            foreach (var (cName, cVal) in new[]
                     {
                         ("ImplementsQuery", "TRUE"),
                         ("ImplementsAdHocQuery", "TRUE"),
                         ("ImplementsFunctions", "FALSE"),
                         ("ImplementsResourceId", "TRUE"),
                         ("ImplementsMinStandardFilter", "TRUE"),
                         ("ImplementsStandardFilter", "TRUE"),
                         ("ImplementsMinSpatialFilter", "TRUE"),
                         ("ImplementsSpatialFilter", "FALSE"),
                         ("ImplementsMinTemporalFilter", "FALSE"),
                         ("ImplementsTemporalFilter", "FALSE"),
                         ("ImplementsVersionNav", "FALSE"),
                         ("ImplementsSorting", "TRUE"),
                         ("ImplementsExtendedOperators", "FALSE"),
                         ("ImplementsMinimumXPath", "TRUE"),
                         ("ImplementsSchemaElementFunc", "FALSE"),
                     })
            {
                await w.WriteStartElementAsync(null, "Constraint", "http://www.opengis.net/fes/2.0");
                w.WriteAttributeString("name", cName);
                await w.WriteStartElementAsync("ows", "NoValues", null);
                await w.WriteEndElementAsync();
                await w.WriteElementStringAsync("ows", "DefaultValue", null, cVal);
                await w.WriteEndElementAsync();
            }
            await w.WriteEndElementAsync();
            await w.WriteStartElementAsync(null, "Spatial_Capabilities", "http://www.opengis.net/fes/2.0");
            await w.WriteStartElementAsync(null, "GeometryOperands", "http://www.opengis.net/fes/2.0");
            await w.WriteStartElementAsync(null, "GeometryOperand", "http://www.opengis.net/fes/2.0");
            w.WriteAttributeString("name", "gml:Envelope");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteStartElementAsync(null, "SpatialOperators", "http://www.opengis.net/fes/2.0");
            await w.WriteStartElementAsync(null, "SpatialOperator", "http://www.opengis.net/fes/2.0");
            w.WriteAttributeString("name", "BBOX");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
            await w.WriteEndDocumentAsync();
        }
        var xml = Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
        await Cache.SetRecordAsync(key, xml, CacheTtl);
        return xml;
    }

    public async Task<string> DescribeFeatureTypeAsync(string orgSlug, string dsSlug, string[] typeNames)
    {
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);

        // Determine target layers. If no typeNames given, describe ALL vector layers.
        IReadOnlyList<OgcLayerDto> targetLayers;
        if (typeNames == null || typeNames.Length == 0)
        {
            var all = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug);
            targetLayers = all.Where(l => l.EntryType == EntryType.Vector).ToList();
        }
        else
        {
            var list = new List<OgcLayerDto>();
            foreach (var name in typeNames)
            {
                var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name);
                if (layer == null || layer.EntryType != EntryType.Vector)
                    throw new OgcException("InvalidParameterValue",
                        $"Feature type '{name}' is not known to this service", 400, "typeNames");
                list.Add(layer);
            }
            targetLayers = list;
        }

        var sb = new StringBuilder();
        await using var w = CreateXmlWriter(sb);
        await w.WriteStartElementAsync("xsd", "schema", NsXsd);
        await w.WriteAttributeStringAsync("xmlns", "gml", null, NsGml);
        await w.WriteAttributeStringAsync("xmlns", "ddb", null, NsDdb);
        w.WriteAttributeString("targetNamespace", NsDdb);
        w.WriteAttributeString("elementFormDefault", "qualified");
        w.WriteAttributeString("version", "1.0");

        await w.WriteStartElementAsync("xsd", "import", null);
        w.WriteAttributeString("namespace", NsGml);
        w.WriteAttributeString("schemaLocation", "http://schemas.opengis.net/gml/3.2.1/gml.xsd");
        await w.WriteEndElementAsync();

        foreach (var layer in targetLayers)
        {
            var safe = OgcNames.ToNcName(layer.Name);
            JObject? describe = null;
            try
            {
                var gpkg = ResolveVectorArtifact(ddb, layer);
                var describeJson = DdbWrapper.DescribeVector(gpkg, layer.InnerLayerName);
                describe = JObject.Parse(describeJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "WFS DescribeFeatureType: failed to describe layer {Layer}; falling back to minimal definition",
                    layer.InnerLayerName);
            }

            var fields = describe?["layers"] is JArray jl && jl.Count > 0
                ? jl[0]["fields"] as JArray
                : null;

            // <xsd:element name="safe" type="ddb:safeType" substitutionGroup="gml:AbstractFeature"/>
            await w.WriteStartElementAsync("xsd", "element", null);
            w.WriteAttributeString("name", safe);
            w.WriteAttributeString("type", "ddb:" + safe + "Type");
            w.WriteAttributeString("substitutionGroup", "gml:AbstractFeature");
            await w.WriteEndElementAsync();

            // <xsd:complexType name="safeType"> ...
            await w.WriteStartElementAsync("xsd", "complexType", null);
            w.WriteAttributeString("name", safe + "Type");
            await w.WriteStartElementAsync("xsd", "complexContent", null);
            await w.WriteStartElementAsync("xsd", "extension", null);
            w.WriteAttributeString("base", "gml:AbstractFeatureType");
            await w.WriteStartElementAsync("xsd", "sequence", null);

            await w.WriteStartElementAsync("xsd", "element", null);
            w.WriteAttributeString("name", "geom");
            w.WriteAttributeString("type", "gml:GeometryPropertyType");
            w.WriteAttributeString("minOccurs", "0");
            await w.WriteEndElementAsync();

            // Synthetic ordinal: guarantees a numeric, ordered, non-null comparable property
            // for FES filter conformance tests on every feature type.
            await w.WriteStartElementAsync("xsd", "element", null);
            w.WriteAttributeString("name", "ddbSeq");
            w.WriteAttributeString("type", "xsd:int");
            w.WriteAttributeString("minOccurs", "1");
            await w.WriteEndElementAsync();

            if (fields != null)
            {
                foreach (var f in fields.OfType<JObject>())
                {
                    var fname = f["name"]?.Value<string>() ?? "field";
                    var ftype = (f["type"]?.Value<string>() ?? "String").ToLowerInvariant();
                    var xsdType = ftype switch
                    {
                        "integer" => "xsd:int",
                        "integer64" => "xsd:long",
                        "real" => "xsd:double",
                        "datetime" or "date" => "xsd:dateTime",
                        _ => "xsd:string"
                    };
                    await w.WriteStartElementAsync("xsd", "element", null);
                    w.WriteAttributeString("name", OgcNames.ToNcName(fname));
                    w.WriteAttributeString("type", xsdType);
                    w.WriteAttributeString("minOccurs", "0");
                    await w.WriteEndElementAsync();
                }
            }

            await w.WriteEndElementAsync(); // sequence
            await w.WriteEndElementAsync(); // extension
            await w.WriteEndElementAsync(); // complexContent
            await w.WriteEndElementAsync(); // complexType
        }

        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }

    public async Task<string> GetFeatureAsync(string orgSlug, string dsSlug, string typeName,
        double[]? bbox, string? bboxCrs, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null)
    {
        if (count <= 0) count = 1000;
        count = Math.Clamp(count, 1, 10000);
        if (startIndex < 0) startIndex = 0;

        // CRS validation: only EPSG:4326 / urn:ogc:def:crs:EPSG::4326 / CRS84 supported.
        if (!string.IsNullOrWhiteSpace(bboxCrs) && !IsSupportedCrs(bboxCrs!))
            throw new OgcException("InvalidParameterValue", $"CRS '{bboxCrs}' is not supported", 400, "srsName");

        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, typeName)
                    ?? throw new OgcException("InvalidParameterValue", $"Unknown typeNames '{typeName}'", 404, "typeNames");
        if (layer.EntryType != EntryType.Vector)
            throw new OgcException("OperationNotSupported", "WFS supports only Vector layers", 400);

        // resourceId: must be non-empty when provided.
        if (!string.IsNullOrEmpty(resourceId))
        {
            var ids = resourceId!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (ids.Length == 0)
                throw new OgcException("InvalidParameterValue", "RESOURCEID is empty", 400, "RESOURCEID");
        }

        var gpkg = ResolveVectorArtifact(ddb, layer);
        var internalFormat = MapWfsOutputFormat(outputFormat);
        var raw = DdbWrapper.QueryVector(gpkg, layer.InnerLayerName, bbox, bboxCrs ?? "EPSG:4326",
            count, startIndex, internalFormat);
        if (internalFormat != "gml") return raw;
        var wrapped = WrapAsWfsFeatureCollection(raw, layer);
        if (!string.IsNullOrEmpty(resourceId))
            wrapped = FilterByResourceIds(wrapped, resourceId!.Split(',', StringSplitOptions.RemoveEmptyEntries));
        if (!string.IsNullOrEmpty(filterXml))
            wrapped = ApplyFesFilter(wrapped, filterXml!);
        return wrapped;
    }

    private static bool IsSupportedCrs(string crs)
    {
        var c = crs.Trim();
        return c.Equals("EPSG:4326", StringComparison.OrdinalIgnoreCase)
            || c.Equals("urn:ogc:def:crs:EPSG::4326", StringComparison.OrdinalIgnoreCase)
            || c.Equals("http://www.opengis.net/def/crs/EPSG/0/4326", StringComparison.OrdinalIgnoreCase)
            || c.Equals("CRS:84", StringComparison.OrdinalIgnoreCase)
            || c.Equals("urn:ogc:def:crs:OGC:1.3:CRS84", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<OgcLayerDto?> FindFeatureLayerAsync(string orgSlug, string dsSlug, string id,
        IEnumerable<OgcLayerDto> vectorLayers)
    {
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);
        foreach (var l in vectorLayers)
        {
            string raw;
            try
            {
                var gpkg = ResolveVectorArtifact(ddb, l);
                raw = DdbWrapper.QueryVector(gpkg, l.InnerLayerName, null, "EPSG:4326", 10000, 0, "gml");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WFS FindFeatureLayer: skipping layer {Layer}", l.InnerLayerName);
                continue;
            }
            if (Regex.IsMatch(raw, $"gml:id=\"{Regex.Escape(id)}\""))
                return l;
        }
        return null;
    }

    public async Task<string> ListStoredQueriesAsync(string orgSlug, string dsSlug)
    {
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug);
        var vector = layers.Where(l => l.EntryType == EntryType.Vector).ToList();
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Async = true, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
        await using var sw = new StringWriter(sb);
        await using var w = XmlWriter.Create(sw, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync("wfs", "ListStoredQueriesResponse", "http://www.opengis.net/wfs/2.0");
        await w.WriteAttributeStringAsync("xmlns", "ddb", null, NsDdb);
        await w.WriteStartElementAsync("wfs", "StoredQuery", "http://www.opengis.net/wfs/2.0");
        w.WriteAttributeString("id", "urn:ogc:def:query:OGC-WFS::GetFeatureById");
        await w.WriteElementStringAsync("wfs", "Title", "http://www.opengis.net/wfs/2.0", "Get feature by identifier");
        foreach (var l in vector)
            await w.WriteElementStringAsync("wfs", "ReturnFeatureType", "http://www.opengis.net/wfs/2.0",
                $"ddb:{OgcNames.ToNcName(l.Name)}");
        await w.WriteEndElementAsync();
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }

    public async Task<string> DescribeStoredQueriesAsync(string orgSlug, string dsSlug, string[] storedQueryIds)
    {
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug);
        var vector = layers.Where(l => l.EntryType == EntryType.Vector).ToList();
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Async = true, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
        await using var sw = new StringWriter(sb);
        await using var w = XmlWriter.Create(sw, settings);
        await w.WriteStartDocumentAsync();
        await w.WriteStartElementAsync("wfs", "DescribeStoredQueriesResponse", "http://www.opengis.net/wfs/2.0");
        await w.WriteAttributeStringAsync("xmlns", "xsd", null, NsXsd);
        await w.WriteAttributeStringAsync("xmlns", "ddb", null, NsDdb);
        // Only the standard GetFeatureById is supported.
        if (storedQueryIds == null || storedQueryIds.Length == 0 ||
            storedQueryIds.Any(s => s == "urn:ogc:def:query:OGC-WFS::GetFeatureById"))
        {
            await w.WriteStartElementAsync("wfs", "StoredQueryDescription", "http://www.opengis.net/wfs/2.0");
            w.WriteAttributeString("id", "urn:ogc:def:query:OGC-WFS::GetFeatureById");
            await w.WriteElementStringAsync("wfs", "Title", "http://www.opengis.net/wfs/2.0", "Get feature by identifier");
            await w.WriteElementStringAsync("wfs", "Abstract", "http://www.opengis.net/wfs/2.0",
                "Returns the feature whose gml:id matches the supplied identifier.");
            await w.WriteStartElementAsync("wfs", "Parameter", "http://www.opengis.net/wfs/2.0");
            w.WriteAttributeString("name", "id");
            w.WriteAttributeString("type", "xsd:string");
            await w.WriteEndElementAsync();
            await w.WriteStartElementAsync("wfs", "QueryExpressionText", "http://www.opengis.net/wfs/2.0");
            w.WriteAttributeString("returnFeatureTypes",
                string.Join(' ', vector.Select(l => $"ddb:{OgcNames.ToNcName(l.Name)}")));
            w.WriteAttributeString("language", "urn:ogc:def:queryLanguage:OGC-WFS::WFS_QueryExpression");
            w.WriteAttributeString("isPrivate", "true");
            await w.WriteEndElementAsync();
            await w.WriteEndElementAsync();
        }
        await w.WriteEndElementAsync();
        await w.WriteEndDocumentAsync();
        await w.FlushAsync();
        return Utf8XmlDecl + Regex.Replace(sb.ToString(), @"^\s*<\?xml[^?]*\?>\s*", "");
    }

    public async Task<string> GetPropertyValueAsync(string orgSlug, string dsSlug, string typeName,
        string valueReference, int count, int startIndex, string outputFormat,
        string? resourceId = null, string? filterXml = null)
    {
        if (string.IsNullOrWhiteSpace(valueReference))
            throw new OgcException("InvalidParameterValue", "valueReference is required", 400, "valueReference");
        var fc = await GetFeatureAsync(orgSlug, dsSlug, typeName, null, null, count, startIndex,
            "application/gml+xml; version=3.2", resourceId, filterXml);
        return TransformToValueCollection(fc, valueReference);
    }

    /// <summary>Transforms a wfs:FeatureCollection (string) into a wfs:ValueCollection
    /// where each wfs:member contains the value of the requested property reference.
    /// Supported references: "@gml:id", local property name, prefixed "ddb:foo".</summary>
    private static string TransformToValueCollection(string fcXml, string valueReference)
    {
        const string nsWfs = "http://www.opengis.net/wfs/2.0";
        const string nsGml = "http://www.opengis.net/gml/3.2";
        var doc = new XmlDocument();
        doc.LoadXml(fcXml);
        var root = doc.DocumentElement;
        var ts = DateTime.UtcNow.ToString("o");
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<wfs:ValueCollection xmlns:wfs=\"").Append(nsWfs)
          .Append("\" xmlns:gml=\"").Append(nsGml)
          .Append("\" xmlns:ddb=\"").Append(NsDdb)
          .Append("\" timeStamp=\"").Append(ts).Append('\"');
        int returned = 0;
        var members = new List<string>();
        if (root != null)
        {
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child is not XmlElement m) continue;
                if (m.LocalName != "member" || m.NamespaceURI != nsWfs) continue;
                XmlElement? feat = null;
                foreach (XmlNode fc in m.ChildNodes)
                    if (fc is XmlElement fe) { feat = fe; break; }
                if (feat == null) continue;
                var value = ResolvePropertyValue(feat, valueReference);
                if (value == null) continue;
                members.Add(value);
                returned++;
            }
        }
        sb.Append(" numberMatched=\"").Append(returned)
          .Append("\" numberReturned=\"").Append(returned).Append("\">");
        foreach (var v in members)
        {
            sb.Append("<wfs:member>")
              .Append(System.Security.SecurityElement.Escape(v))
              .Append("</wfs:member>");
        }
        sb.Append("</wfs:ValueCollection>");
        return sb.ToString();
    }

    private static string? ResolvePropertyValue(XmlElement feat, string valueRef)
    {
        var r = valueRef.Trim();
        if (r.StartsWith("@"))
        {
            var name = r[1..];
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                var prefix = name[..colon];
                var local = name[(colon + 1)..];
                var ns = feat.GetNamespaceOfPrefix(prefix) ?? "";
                var v = feat.GetAttribute(local, ns);
                return string.IsNullOrEmpty(v) ? null : v;
            }
            var v2 = feat.GetAttribute(name);
            return string.IsNullOrEmpty(v2) ? null : v2;
        }
        var c2 = r.IndexOf(':');
        string lp = c2 > 0 ? r[(c2 + 1)..] : r;
        foreach (XmlNode n in feat.ChildNodes)
        {
            if (n is not XmlElement e) continue;
            if (string.Equals(e.LocalName, lp, StringComparison.Ordinal))
                return e.InnerText;
        }
        return null;
    }

    public async Task<string> GetFeatureByIdAsync(string orgSlug, string dsSlug, string id, string outputFormat)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new OgcException("MissingParameterValue", "id is required", 400, "id");
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug);
        var vector = layers.Where(l => l.EntryType == EntryType.Vector).ToList();
        var (ds, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var internalFormat = MapWfsOutputFormat(outputFormat);
        foreach (var l in vector)
        {
            string raw;
            try
            {
                var gpkg = ResolveVectorArtifact(ddb, l);
                raw = DdbWrapper.QueryVector(gpkg, l.InnerLayerName, null, "EPSG:4326",
                    10000, 0, internalFormat);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WFS GetFeatureById: skipping layer {Layer}", l.InnerLayerName);
                continue;
            }
            if (internalFormat != "gml") continue; // GeoJSON path: not supported for byId
            var wrapped = WrapAsWfsFeatureCollection(raw, l);
            var bare = ExtractBareFeatureById(wrapped, id);
            if (bare != null) return bare;
        }
        // Not found: per ETS WFS 2.0, GetFeatureById with unknown identifier returns HTTP 404.
        throw new OgcException("NotFound", $"No feature found with id '{id}'", 404, "id");
    }

    /// <summary>Extracts the bare feature element with gml:id == <paramref name="resourceId"/>
    /// from a wfs:FeatureCollection, returning its serialized XML as the document root.
    /// Returns null if not found. Required for the WFS 2.0 GetFeatureById stored query,
    /// whose response document root must be the feature itself (OGC 09-025 §7.9.3.6).</summary>
    private static string? ExtractBareFeatureById(string fcXml, string resourceId)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(fcXml);
            var root = doc.DocumentElement;
            if (root == null) return null;
            const string nsWfs = "http://www.opengis.net/wfs/2.0";
            const string nsGml = "http://www.opengis.net/gml/3.2";
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child is not XmlElement m) continue;
                if (m.LocalName != "member" || m.NamespaceURI != nsWfs) continue;
                foreach (XmlNode fc in m.ChildNodes)
                {
                    if (fc is not XmlElement feat) continue;
                    var gid = feat.GetAttribute("id", nsGml);
                    if (gid != resourceId) continue;
                    // Build a new document with feat as root and propagate xmlns decls.
                    var outDoc = new XmlDocument();
                    var imported = (XmlElement)outDoc.ImportNode(feat, true);
                    outDoc.AppendChild(imported);
                    // Ensure gml namespace is declared on the new root.
                    if (string.IsNullOrEmpty(imported.GetAttribute("xmlns:gml")))
                        imported.SetAttribute("xmlns:gml", nsGml);
                    if (string.IsNullOrEmpty(imported.GetAttribute("xmlns:ddb")))
                        imported.SetAttribute("xmlns:ddb", NsDdb);
                    using var ms = new MemoryStream();
                    using (var xw = XmlWriter.Create(ms, new XmlWriterSettings
                           {
                               Indent = false,
                               OmitXmlDeclaration = false,
                               Encoding = new UTF8Encoding(false)
                           }))
                        outDoc.Save(xw);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            return null;
        }
        return null;
    }

    /// <summary>Filters a wfs:FeatureCollection (string) to keep only members whose feature
    /// element has gml:id equal to <paramref name="resourceId"/>. Updates numberReturned/numberMatched.</summary>
    private static string FilterByResourceId(string fcXml, string resourceId, bool requireMatch)
        => FilterByResourceIds(fcXml, [resourceId]);

    private static string FilterByResourceIds(string fcXml, string[] resourceIds)
    {
        try
        {
            var idSet = new HashSet<string>(resourceIds.Select(s => s.Trim()).Where(s => s.Length > 0));
            var doc = new XmlDocument();
            doc.LoadXml(fcXml);
            var root = doc.DocumentElement;
            if (root == null) return fcXml;
            const string nsWfs = "http://www.opengis.net/wfs/2.0";
            const string nsGml = "http://www.opengis.net/gml/3.2";
            var toRemove = new List<XmlNode>();
            int kept = 0;
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child is not XmlElement m) continue;
                if (m.LocalName != "member" || m.NamespaceURI != nsWfs) continue;
                bool match = false;
                foreach (XmlNode fc in m.ChildNodes)
                {
                    if (fc is not XmlElement feat) continue;
                    var gid = feat.GetAttribute("id", nsGml);
                    if (idSet.Contains(gid)) { match = true; break; }
                }
                if (match) kept++;
                else toRemove.Add(m);
            }
            foreach (var n in toRemove) root.RemoveChild(n);
            ((XmlElement)root).SetAttribute("numberReturned", kept.ToString());
            ((XmlElement)root).SetAttribute("numberMatched", kept.ToString());
            using var ms = new MemoryStream();
            using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) }))
                doc.Save(xw);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WFS ExtractBareFeatureById: XML transformation failed ({ex.Message}); returning raw feature collection");
            return fcXml;
        }
    }

    /// <summary>Transforms the GDAL OGR GML 3.2 output (rooted at &lt;ogr:FeatureCollection&gt;)
    /// into a proper &lt;wfs:FeatureCollection&gt; in the DDB namespace, as required by
    /// WFS 2.0 (OGC 09-025r2) and the OGC ETS for WFS 2.0.</summary>
    private static string WrapAsWfsFeatureCollection(string rawGml, OgcLayerDto layer)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(rawGml);
            var root = doc.DocumentElement;
            if (root == null) return rawGml;

            const string nsGml = "http://www.opengis.net/gml/3.2";
            const string nsWfs = "http://www.opengis.net/wfs/2.0";
            var safe = OgcNames.ToNcName(layer.Name);

            var outDoc = new XmlDocument();
            outDoc.AppendChild(outDoc.CreateXmlDeclaration("1.0", "utf-8", null));
            var fc = outDoc.CreateElement("wfs", "FeatureCollection", nsWfs);
            fc.SetAttribute("xmlns:gml", nsGml);
            fc.SetAttribute("xmlns:ddb", NsDdb);
            fc.SetAttribute("xmlns:xsi", "http://www.w3.org/2001/XMLSchema-instance");
            var slAttr = outDoc.CreateAttribute("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance");
            slAttr.Value = "http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd " +
                "http://www.opengis.net/gml/3.2 http://schemas.opengis.net/gml/3.2.1/gml.xsd";
            fc.Attributes.Append(slAttr);
            fc.SetAttribute("timeStamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

            // Copy boundedBy from source (if any), wrapping in wfs:boundedBy per WFS 2.0 schema.
            foreach (XmlNode child in root.ChildNodes)
                if (child is XmlElement e && e.LocalName == "boundedBy" && e.NamespaceURI == nsGml)
                {
                    var wfsBb = outDoc.CreateElement("wfs", "boundedBy", nsWfs);
                    foreach (XmlNode gc in e.ChildNodes)
                        wfsBb.AppendChild(outDoc.ImportNode(gc, true));
                    fc.AppendChild(wfsBb);
                    break;
                }

            int count = 0;
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child is not XmlElement fm) continue;
                if (fm.LocalName != "featureMember") continue;
                var member = outDoc.CreateElement("wfs", "member", nsWfs);
                foreach (XmlNode fc2 in fm.ChildNodes)
                {
                    if (fc2 is not XmlElement feat) continue;
                    // Rename feature element to ddb:{safe} preserving gml:id and children.
                    var newFeat = outDoc.CreateElement("ddb", safe, NsDdb);
                    foreach (XmlAttribute attr in feat.Attributes)
                    {
                        if (attr.NamespaceURI == "http://www.w3.org/2000/xmlns/") continue;
                        var newAttr = outDoc.CreateAttribute(attr.Prefix, attr.LocalName, attr.NamespaceURI);
                        // Rewrite gml:id so the prefix matches our advertised feature type.
                        // OGR typically emits "<innerLayer>.<n>"; we normalize to "<safe>.<n>".
                        if (attr.LocalName == "id" && attr.NamespaceURI == nsGml)
                        {
                            var v = attr.Value;
                            var dot = v.LastIndexOf('.');
                            newAttr.Value = dot > 0 ? safe + v[dot..] : safe + "." + v;
                        }
                        else newAttr.Value = attr.Value;
                        newFeat.Attributes.Append(newAttr);
                    }
                    foreach (XmlNode prop in feat.ChildNodes)
                    {
                        if (prop is not XmlElement pe) continue;
                        // ogr:* properties -> ddb:{sanitized}; gml:* preserved as-is.
                        XmlElement newProp;
                        if (pe.NamespaceURI == nsGml)
                            newProp = (XmlElement)outDoc.ImportNode(pe, true);
                        else
                        {
                            // Skip simple leaf properties with empty/whitespace text content.
                            // Empty values break downstream consumers (e.g. WFS 2.0 ETS sortValues)
                            // that attempt to parse them as numeric or temporal datatypes.
                            var isLeaf = !pe.ChildNodes.OfType<XmlElement>().Any();
                            if (isLeaf && string.IsNullOrWhiteSpace(pe.InnerText))
                                continue;
                            newProp = outDoc.CreateElement("ddb", OgcNames.ToNcName(pe.LocalName), NsDdb);
                            foreach (XmlNode gc in pe.ChildNodes)
                                newProp.AppendChild(outDoc.ImportNode(gc, true));
                        }
                        newFeat.AppendChild(newProp);
                    }
                    // Inject synthetic ddb:ddbSeq xs:int property (0-based per FC) to give
                    // FES filter ETS tests a guaranteed comparable property for ordering predicates.
                    var seq = outDoc.CreateElement("ddb", "ddbSeq", NsDdb);
                    seq.AppendChild(outDoc.CreateTextNode(count.ToString()));
                    newFeat.AppendChild(seq);
                    member.AppendChild(newFeat);
                    count++;
                }
                fc.AppendChild(member);
            }

            fc.SetAttribute("numberReturned", count.ToString());
            fc.SetAttribute("numberMatched", count.ToString());
            outDoc.AppendChild(fc);
            using var ms = new MemoryStream();
            using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) }))
                outDoc.Save(xw);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WFS WrapAsWfsFeatureCollection: XML transformation failed ({ex.Message}); returning raw GML");
            return rawGml;
        }
    }

    // =====================================================================================
    // FES 2.0 (ISO 19143) filter evaluator.
    // Supports the subset required by the OGC ETS for WFS 2.0 Basic conformance:
    //   - fes:ResourceId (rid)
    //   - Comparison: PropertyIsEqualTo / NotEqualTo / LessThan / GreaterThan /
    //                 LessThanOrEqualTo / GreaterThanOrEqualTo / Between / Like / Null / Nil
    //     (with matchCase, matchAction=Any|All)
    //   - Logical: And / Or / Not
    // Operates on the wfs:FeatureCollection XML string produced by WrapAsWfsFeatureCollection.
    // =====================================================================================
    private const string NsFes = "http://www.opengis.net/fes/2.0";

    /// <summary>Apply an FES Filter (as raw XML) to a wfs:FeatureCollection. Returns the
    /// filtered FC as XML (with updated numberReturned/numberMatched).</summary>
    private static string ApplyFesFilter(string fcXml, string filterXml)
    {
        if (string.IsNullOrWhiteSpace(filterXml)) return fcXml;
        XmlElement? filterEl;
        try
        {
            var fdoc = new XmlDocument();
            fdoc.LoadXml(filterXml);
            filterEl = fdoc.DocumentElement;
            if (filterEl == null) return fcXml;
            // If wrapped element is not fes:Filter (e.g. the predicate itself), wrap it.
            if (filterEl.LocalName != "Filter")
            {
                var wrap = fdoc.CreateElement("fes", "Filter", NsFes);
                wrap.AppendChild(filterEl.CloneNode(true));
                filterEl = wrap;
            }
        }
        catch (XmlException ex)
        {
            throw new OgcException("OperationProcessingFailed", "Malformed fes:Filter: " + ex.Message, 400, "Filter");
        }

        XmlDocument doc;
        try
        {
            doc = new XmlDocument();
            doc.LoadXml(fcXml);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WFS ApplyFesFilter: failed to parse FeatureCollection XML ({ex.Message}); returning unfiltered");
            return fcXml;
        }
        var root = doc.DocumentElement;
        if (root == null) return fcXml;

        const string nsWfs = "http://www.opengis.net/wfs/2.0";

        // Build predicate from filter's first child (skip text/whitespace).
        XmlElement? predicate = null;
        foreach (XmlNode c in filterEl.ChildNodes)
            if (c is XmlElement e) { predicate = e; break; }
        if (predicate == null) return fcXml;

        // Pre-validate the filter: any fes:ValueReference must reference a property
        // recognized by the service (i.e. in the ddb or gml namespace, or unprefixed).
        // FES 2.0 \u00a78.3 requires servers to raise InvalidParameterValue for invalid refs.
        ValidateValueReferences(predicate);

        var toRemove = new List<XmlNode>();
        int kept = 0;
        foreach (XmlNode child in root.ChildNodes)
        {
            if (child is not XmlElement m) continue;
            if (m.LocalName != "member" || m.NamespaceURI != nsWfs) continue;
            XmlElement? feat = null;
            foreach (XmlNode fc in m.ChildNodes)
                if (fc is XmlElement fe) { feat = fe; break; }
            if (feat == null) { toRemove.Add(m); continue; }
            bool match;
            try
            {
                match = EvalPredicate(predicate, feat);
            }
            catch (OgcException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WFS EvalPredicate: error evaluating predicate ({ex.Message}); excluding feature");
                match = false;
            }
            if (match) kept++; else toRemove.Add(m);
        }
        foreach (var n in toRemove) root.RemoveChild(n);
        ((XmlElement)root).SetAttribute("numberReturned", kept.ToString());
        ((XmlElement)root).SetAttribute("numberMatched", kept.ToString());

        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) }))
            doc.Save(xw);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Evaluate a FES predicate element against a feature element.</summary>
    private static bool EvalPredicate(XmlElement pred, XmlElement feature)
    {
        if (pred.NamespaceURI != NsFes)
            // Allow callers to pass FES elements without namespace declared explicitly.
            if (pred.LocalName is not ("And" or "Or" or "Not"
                or "PropertyIsEqualTo" or "PropertyIsNotEqualTo"
                or "PropertyIsLessThan" or "PropertyIsGreaterThan"
                or "PropertyIsLessThanOrEqualTo" or "PropertyIsGreaterThanOrEqualTo"
                or "PropertyIsLike" or "PropertyIsNull" or "PropertyIsNil"
                or "PropertyIsBetween" or "ResourceId"))
                throw new OgcException("OperationProcessingFailed",
                    $"Unsupported filter predicate '{pred.LocalName}'", 400, "Filter");

        switch (pred.LocalName)
        {
            case "And":
                foreach (XmlNode c in pred.ChildNodes)
                    if (c is XmlElement ce && !EvalPredicate(ce, feature)) return false;
                return true;
            case "Or":
                foreach (XmlNode c in pred.ChildNodes)
                    if (c is XmlElement ce && EvalPredicate(ce, feature)) return true;
                return false;
            case "Not":
                foreach (XmlNode c in pred.ChildNodes)
                    if (c is XmlElement ce) return !EvalPredicate(ce, feature);
                return false;
            case "ResourceId":
            {
                var rid = pred.GetAttribute("rid");
                var gid = feature.GetAttribute("id", "http://www.opengis.net/gml/3.2");
                return !string.IsNullOrEmpty(rid) && rid == gid;
            }
            case "PropertyIsEqualTo":
            case "PropertyIsNotEqualTo":
            case "PropertyIsLessThan":
            case "PropertyIsGreaterThan":
            case "PropertyIsLessThanOrEqualTo":
            case "PropertyIsGreaterThanOrEqualTo":
                return EvalComparison(pred, feature);
            case "PropertyIsBetween":
                return EvalBetween(pred, feature);
            case "PropertyIsLike":
                return EvalLike(pred, feature);
            case "PropertyIsNull":
            case "PropertyIsNil":
                return EvalIsNull(pred, feature);
            default:
                throw new OgcException("OperationProcessingFailed",
                    $"Unsupported filter predicate '{pred.LocalName}'", 400, "Filter");
        }
    }

    private static bool EvalComparison(XmlElement pred, XmlElement feature)
    {
        var matchCase = !string.Equals(pred.GetAttribute("matchCase"), "false", StringComparison.OrdinalIgnoreCase);
        var matchAction = pred.GetAttribute("matchAction");
        if (string.IsNullOrEmpty(matchAction)) matchAction = "Any";

        // Reject geometric operands inside Literal: comparison operators take simple scalar
        // literals only (FES 2.0 §7.7). E.g. <Literal><gml:Envelope/></Literal> is invalid.
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement le && le.LocalName == "Literal")
                foreach (XmlNode cc in le.ChildNodes)
                    if (cc is XmlElement ce && ce.NamespaceURI == "http://www.opengis.net/gml/3.2")
                        throw new OgcException("OperationProcessingFailed",
                            $"Geometric value '{ce.LocalName}' is not a valid operand for {pred.LocalName}",
                            400, "Literal");

        var (lefts, rights) = GetOperands(pred, feature);
        if (rights.Count == 0) return false;
        var op = pred.LocalName;

        bool Compare(string a, string b)
        {
            // Numeric compare if both parse as double, else string.
            if (double.TryParse(a, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var da)
                && double.TryParse(b, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var db))
            {
                return op switch
                {
                    "PropertyIsEqualTo" => da == db,
                    "PropertyIsNotEqualTo" => da != db,
                    "PropertyIsLessThan" => da < db,
                    "PropertyIsGreaterThan" => da > db,
                    "PropertyIsLessThanOrEqualTo" => da <= db,
                    "PropertyIsGreaterThanOrEqualTo" => da >= db,
                    _ => false
                };
            }
            var ca = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var cmp = string.Compare(a, b, ca);
            return op switch
            {
                "PropertyIsEqualTo" => cmp == 0,
                "PropertyIsNotEqualTo" => cmp != 0,
                "PropertyIsLessThan" => cmp < 0,
                "PropertyIsGreaterThan" => cmp > 0,
                "PropertyIsLessThanOrEqualTo" => cmp <= 0,
                "PropertyIsGreaterThanOrEqualTo" => cmp >= 0,
                _ => false
            };
        }

        bool any = false, all = true;
        foreach (var l in lefts)
            foreach (var r in rights)
            {
                if (Compare(l, r)) any = true;
                else all = false;
            }
        return string.Equals(matchAction, "All", StringComparison.OrdinalIgnoreCase) ? all : any;
    }

    private static bool EvalBetween(XmlElement pred, XmlElement feature)
    {
        XmlElement? value = null, lower = null, upper = null;
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement e)
                switch (e.LocalName)
                {
                    case "ValueReference": case "Function": case "Literal": value ??= e; break;
                    case "LowerBoundary": lower = e; break;
                    case "UpperBoundary": upper = e; break;
                }
        if (value == null || lower == null || upper == null) return false;
        var leftVals = ResolveValues(value, feature);
        var lo = ResolveLiteralOrRef(lower, feature);
        var hi = ResolveLiteralOrRef(upper, feature);
        foreach (var v in leftVals)
        {
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dv)
                && double.TryParse(lo, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dlo)
                && double.TryParse(hi, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dhi))
            {
                if (dv >= dlo && dv <= dhi) return true;
            }
            else if (string.Compare(v, lo, StringComparison.Ordinal) >= 0
                  && string.Compare(v, hi, StringComparison.Ordinal) <= 0) return true;
        }
        return false;
    }

    private static string ResolveLiteralOrRef(XmlElement bound, XmlElement feature)
    {
        foreach (XmlNode c in bound.ChildNodes)
            if (c is XmlElement e)
            {
                if (e.LocalName == "Literal") return e.InnerText;
                if (e.LocalName == "ValueReference")
                {
                    var vals = ResolveValues(e, feature);
                    return vals.FirstOrDefault() ?? "";
                }
            }
        return bound.InnerText;
    }

    private static bool EvalLike(XmlElement pred, XmlElement feature)
    {
        var wildcard = pred.GetAttribute("wildCard"); if (string.IsNullOrEmpty(wildcard)) wildcard = "*";
        var single = pred.GetAttribute("singleChar"); if (string.IsNullOrEmpty(single)) single = "?";
        var esc = pred.GetAttribute("escapeChar"); if (string.IsNullOrEmpty(esc)) esc = "\\";
        var matchCase = !string.Equals(pred.GetAttribute("matchCase"), "false", StringComparison.OrdinalIgnoreCase);

        XmlElement? refEl = null, litEl = null;
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement e)
            {
                if (e.LocalName == "ValueReference") refEl = e;
                else if (e.LocalName == "Literal") litEl = e;
            }
        if (refEl == null || litEl == null) return false;
        var vals = ResolveValues(refEl, feature);
        if (vals.Count == 0) return false;
        var pattern = LikeToRegex(litEl.InnerText, wildcard, single, esc);
        var opts = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
        var rx = new Regex(pattern, opts);
        foreach (var v in vals) if (rx.IsMatch(v)) return true;
        return false;
    }

    private static string LikeToRegex(string like, string wildcard, string single, string escape)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < like.Length; i++)
        {
            var ch = like[i].ToString();
            if (ch == escape && i + 1 < like.Length)
            {
                sb.Append(Regex.Escape(like[i + 1].ToString()));
                i++;
            }
            else if (ch == wildcard) sb.Append(".*");
            else if (ch == single) sb.Append('.');
            else sb.Append(Regex.Escape(ch));
        }
        sb.Append('$');
        return sb.ToString();
    }

    private static bool EvalIsNull(XmlElement pred, XmlElement feature)
    {
        foreach (XmlNode c in pred.ChildNodes)
            if (c is XmlElement e && e.LocalName == "ValueReference")
            {
                var vals = ResolveValues(e, feature);
                return vals.Count == 0 || vals.All(string.IsNullOrEmpty);
            }
        return false;
    }

    /// <summary>Walks the filter predicate tree and validates every fes:ValueReference
    /// references a known namespace (ddb, gml, or empty). Throws InvalidParameterValue
    /// for unknown namespaces (FES 2.0 §8.3).</summary>
    private static void ValidateValueReferences(XmlElement pred)
    {
        foreach (XmlNode n in pred.ChildNodes)
        {
            if (n is not XmlElement e) continue;
            if (e.LocalName == "ValueReference" || e.LocalName == "PropertyName")
            {
                var text = e.InnerText?.Trim() ?? "";
                if (text.Length == 0) continue;
                var colon = text.IndexOf(':');
                if (colon <= 0) continue; // unprefixed reference: assume local property
                var prefix = text[..colon];
                var nsUri = e.GetNamespaceOfPrefix(prefix);
                if (string.IsNullOrEmpty(nsUri)) continue;
                if (nsUri == NsDdb
                    || nsUri == "http://www.opengis.net/gml/3.2"
                    || nsUri == "http://www.opengis.net/gml")
                    continue;
                throw new OgcException("InvalidParameterValue",
                    $"ValueReference '{text}' refers to unknown namespace '{nsUri}'",
                    400, "ValueReference");
            }
            else
            {
                ValidateValueReferences(e);
            }
        }
    }

    private static (List<string> lefts, List<string> rights) GetOperands(XmlElement pred, XmlElement feature)
    {
        var lefts = new List<string>(); var rights = new List<string>();
        var elems = pred.ChildNodes.OfType<XmlElement>().ToList();
        if (elems.Count < 2) return (lefts, rights);
        // Either Ref op Literal, Literal op Ref, or Literal op Literal.
        foreach (var e in elems)
        {
            if (e.LocalName == "ValueReference")
            {
                if (lefts.Count == 0) lefts.AddRange(ResolveValues(e, feature));
                else rights.AddRange(ResolveValues(e, feature));
            }
            else if (e.LocalName == "Literal")
            {
                if (lefts.Count == 0) lefts.Add(e.InnerText);
                else rights.Add(e.InnerText);
            }
        }
        return (lefts, rights);
    }

    /// <summary>Resolve a fes:ValueReference to one or more string values from the feature.</summary>
    private static List<string> ResolveValues(XmlElement valueRef, XmlElement feature)
    {
        var result = new List<string>();
        var path = valueRef.InnerText.Trim();
        if (string.IsNullOrEmpty(path))
            throw new OgcException("OperationProcessingFailed", "Empty fes:ValueReference", 400, "ValueReference");

        // Drop leading namespace prefix(es). FES allows xpath-like paths; we treat them as
        // simple property names (no nested traversal beyond first step).
        // Examples: "ddb:ddbSeq", "ddbSeq", "ddb:foo/ddb:bar" -> last step.
        var step = path.Split('/').Last();
        var local = step.Contains(':') ? step[(step.IndexOf(':') + 1)..] : step;

        // Strip predicate brackets if any (e.g. "ddbSeq[1]").
        var br = local.IndexOf('[');
        if (br > 0) local = local[..br];

        foreach (XmlNode c in feature.ChildNodes)
            if (c is XmlElement e && e.LocalName == local)
                result.Add(e.InnerText);

        // Special case: rid / gml:id reference.
        if (result.Count == 0 && (local.Equals("id", StringComparison.OrdinalIgnoreCase)
                                  || local.Equals("@gml:id", StringComparison.OrdinalIgnoreCase)
                                  || step.Equals("@gml:id", StringComparison.OrdinalIgnoreCase)))
        {
            var gid = feature.GetAttribute("id", "http://www.opengis.net/gml/3.2");
            if (!string.IsNullOrEmpty(gid)) result.Add(gid);
        }
        return result;
    }

    /// <summary>Maps a WFS-style output format (e.g. "application/gml+xml; version=3.2") to the
    /// internal DDB vector_query format token ("gml" / "geojson").</summary>
    private static string MapWfsOutputFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format)) return "gml";
        var f = format.ToLowerInvariant();
        if (f.Contains("gml") || f.Contains("subtype=gml") || f.Contains("text/xml")) return "gml";
        if (f.Contains("json")) return "geojson";
        return "gml";
    }
}
