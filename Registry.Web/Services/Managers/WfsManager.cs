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

/// <summary>
/// WFS 2.0.0 manager (BBOX filter + pagination, no CQL).
/// </summary>
public class WfsManager : OgcManagerBase, IWfsManager
{
    private readonly ILogger<WfsManager> _logger;

    public WfsManager(IDatasetAccessResolver access, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx, ILogger<WfsManager> logger)
        : base(access, ar, w, c, cache, ctx)
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
        var key = await LayerCatalog.CacheKeyAsync(orgSlug, dsSlug, "caps-wfs-v2-2.0.0", folderPath);
        var cached = await Cache.GetRecordAsync<string>(key);
        if (cached != null) return cached;

        await ResolveAsync(orgSlug, dsSlug);
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath);
        // Only advertise vector layers that have a built GPKG sidecar. Unbuilt entries would
        // surface 400/InvalidParameterValue on every WFS operation, so we hide them entirely.
        var vectorLayers = layers.Where(l => l.EntryType == EntryType.Vector && l.HasBuiltArtifact).ToList();
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
            await w.WriteElementStringAsync("ows", "Title", null, $"DroneDB WFS - {orgSlug}/{dsSlug}");
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
            targetLayers = [.. all.Where(l => l.EntryType == EntryType.Vector)];
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
            JArray? fields = null;
            try
            {
                fields = LayerFields(ResolveVectorArtifact(ddb, layer), layer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "WFS DescribeFeatureType: failed to describe layer {Layer}; falling back to minimal definition",
                    layer.InnerLayerName);
            }

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
        long[]? fids = null;
        if (!string.IsNullOrEmpty(resourceId))
        {
            var ids = resourceId!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (ids.Length == 0)
                throw new OgcException("InvalidParameterValue", "RESOURCEID is empty", 400, "RESOURCEID");
            fids = FeatureIdsOf(ids, layer);
            if (fids.Length == 0)
                return WfsFeatureCollectionWriter.ToFeatureCollection(EmptyOgrCollection, layer.Name);
        }

        var gpkg = ResolveVectorArtifact(ddb, layer);
        var internalFormat = MapWfsOutputFormat(outputFormat);
        // RESOURCEID selects by feature id in DroneDB (GeoPackage primary key), so paging applies
        // to the selected features. A FES Filter selects before paging too (OGC 09-025r2 §7.9.2.4):
        // DroneDB evaluates the filters FesSqlTranslator can express; the others are evaluated
        // here on up to 10 000 features.
        var query = new VectorQuery(layer.InnerLayerName, bbox, bboxCrs ?? "EPSG:4326", fids, count, startIndex,
            internalFormat);
        if (!string.IsNullOrEmpty(filterXml))
        {
            var filter = ParseFesFilter(filterXml!);
            var sql = TranslateFilter(filter, gpkg, layer, bbox);
            if (sql == null)
                return EvaluateFilterHere(gpkg, query, filter, layer);

            var filtered = query with
            {
                Where = sql.Where,
                Bbox = sql.Bbox ?? query.Bbox,
                BboxSrs = sql.Bbox != null ? sql.BboxCrs : query.BboxSrs
            };
            try
            {
                return QueryPage(gpkg, filtered, layer);
            }
            catch (DdbException ex)
            {
                _logger.LogWarning(ex, "WFS GetFeature: DroneDB could not evaluate {Where}; filtering in the Registry",
                    filtered.Where);
                return EvaluateFilterHere(gpkg, query, filter, layer);
            }
        }

        return QueryPage(gpkg, query, layer);
    }

    /// <summary>The page <paramref name="query"/> selects; GML with numberMatched from DroneDB's count.</summary>
    private string QueryPage(string gpkg, VectorQuery query, OgcLayerDto layer)
    {
        var raw = DdbWrapper.QueryVector(gpkg, query);
        if (query.OutputFormat != "gml") return raw;
        return WfsFeatureCollectionWriter.ToFeatureCollection(raw, layer.Name, DdbWrapper.CountVector(gpkg, query));
    }

    /// <summary>
    /// The SQL form of <paramref name="filter"/> for the layer, or null when DroneDB cannot evaluate it
    /// (see <see cref="FesSqlTranslator"/>) or it has a BBOX while the request has one too.
    /// </summary>
    private FesSqlFilter? TranslateFilter(XmlElement filter, string gpkg, OgcLayerDto layer, double[]? bbox)
    {
        ValidateValueReferences(filter);
        var fieldNames = (LayerFields(gpkg, layer) ?? [])
            .OfType<JObject>()
            .Select(f => f["name"]?.Value<string>())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var sql = FesSqlTranslator.Translate(filter, fieldNames);
        return sql?.Bbox != null && bbox != null ? null : sql;
    }

    /// <summary>
    /// A filter DroneDB cannot evaluate: up to 10 000 features are read as GML and filtered here,
    /// then <paramref name="query"/>'s page of the matches is returned in its format.
    /// </summary>
    private string EvaluateFilterHere(string gpkg, VectorQuery query, XmlElement filter, OgcLayerDto layer)
    {
        var all = DdbWrapper.QueryVector(gpkg, query with { MaxFeatures = 10000, StartIndex = 0, OutputFormat = "gml" });
        var collection = new XmlDocument();
        collection.LoadXml(WfsFeatureCollectionWriter.ToFeatureCollection(all, layer.Name));
        var matches = FilterMembers(collection, filter);

        if (query.OutputFormat == "gml")
            return PageMembers(collection, matches, query.StartIndex, query.MaxFeatures);

        // Other formats are written by DroneDB: read the page of matches by feature id
        var page = matches.Skip(query.StartIndex).Take(query.MaxFeatures)
            .Select(m => WfsFeatureIds.TryParse(FeatureOf(m)?.GetAttribute("id", NsGml), out _, out var fid) ? fid : -1)
            .Where(fid => fid >= 0)
            .ToArray();
        return DdbWrapper.QueryVector(gpkg, query with
        {
            Fids = page.Length > 0 ? page : [-1],
            StartIndex = 0
        });
    }

    /// <summary>The fields of the layer from DroneDB's description, or null when it lists none.</summary>
    private JArray? LayerFields(string gpkg, OgcLayerDto layer)
    {
        var describe = JObject.Parse(DdbWrapper.DescribeVector(gpkg, layer.InnerLayerName));
        return describe["layers"] is JArray layers && layers.Count > 0 ? layers[0]["fields"] as JArray : null;
    }

    /// <summary>An OGR GML response without features.</summary>
    private const string EmptyOgrCollection =
        "<ogr:FeatureCollection xmlns:ogr=\"http://ogr.maptools.org/\" xmlns:gml=\"" + NsGml + "\"/>";

    /// <summary>The fids of the feature ids that belong to <paramref name="layer"/>'s feature type.</summary>
    private static long[] FeatureIdsOf(IEnumerable<string> ids, OgcLayerDto layer)
    {
        var featureType = OgcNames.ToNcName(layer.Name);
        return ids
            .Select(id => WfsFeatureIds.TryParse(id, out var type, out var fid) && type == featureType ? fid : -1)
            .Where(fid => fid >= 0)
            .Distinct()
            .ToArray();
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

        // "<feature type>.<fid>": one primary-key read in the layer(s) of that feature type
        // (two layer names can map to the same NCName)
        if (WfsFeatureIds.TryParse(id, out var featureType, out var fid))
        {
            var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug);
            var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
            var internalFormat = MapWfsOutputFormat(outputFormat);
            foreach (var l in layers.Where(l =>
                         l.EntryType == EntryType.Vector && OgcNames.ToNcName(l.Name) == featureType))
            {
                if (internalFormat != "gml") continue; // GeoJSON path: not supported for byId
                string raw;
                try
                {
                    raw = DdbWrapper.QueryVector(ResolveVectorArtifact(ddb, l),
                        new VectorQuery(l.InnerLayerName, Fids: [fid], MaxFeatures: 1, OutputFormat: internalFormat));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WFS GetFeatureById: skipping layer {Layer}", l.InnerLayerName);
                    continue;
                }

                var feature = WfsFeatureCollectionWriter.ToSingleFeature(raw, l.Name);
                if (feature != null) return feature;
            }
        }

        // Not found: per ETS WFS 2.0, GetFeatureById with unknown identifier returns HTTP 404.
        throw new OgcException("NotFound", $"No feature found with id '{id}'", 404, "id");
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
    private const string NsFes = OgcNamespaces.Fes;

    /// <summary>Parses a FES filter (fes:Filter, or a predicate, which is wrapped in one).</summary>
    private static XmlElement ParseFesFilter(string filterXml)
    {
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(filterXml);
            var filter = doc.DocumentElement
                         ?? throw new OgcException("OperationProcessingFailed", "Empty fes:Filter", 400, "Filter");
            if (filter.LocalName == "Filter")
                return filter;

            var wrap = doc.CreateElement("fes", "Filter", NsFes);
            wrap.AppendChild(filter.CloneNode(true));
            return wrap;
        }
        catch (XmlException ex)
        {
            throw new OgcException("OperationProcessingFailed", "Malformed fes:Filter: " + ex.Message, 400, "Filter");
        }
    }

    /// <summary>The wfs:member elements of <paramref name="collection"/> whose feature matches the filter.</summary>
    private static List<XmlElement> FilterMembers(XmlDocument collection, XmlElement filter)
    {
        var matches = new List<XmlElement>();
        var predicate = filter.ChildNodes.OfType<XmlElement>().FirstOrDefault();
        var root = collection.DocumentElement;
        if (root == null)
            return matches;

        // FES 2.0 §8.3: references outside the service's namespaces are InvalidParameterValue
        if (predicate != null)
            ValidateValueReferences(predicate);

        foreach (var member in Members(root))
        {
            var feature = FeatureOf(member);
            if (feature == null)
                continue;
            if (predicate == null)
            {
                matches.Add(member);
                continue;
            }

            bool match;
            try
            {
                match = EvalPredicate(predicate, feature);
            }
            catch (OgcException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WFS EvalPredicate: error evaluating predicate ({ex.Message}); excluding feature");
                match = false;
            }
            if (match) matches.Add(member);
        }
        return matches;
    }

    /// <summary>
    /// <paramref name="collection"/> with only the page [startIndex, startIndex + count) of
    /// <paramref name="matches"/>: numberMatched counts every match, numberReturned the page. The
    /// features keep the ddbSeq of the unfiltered collection, which filters may have referenced.
    /// </summary>
    private static string PageMembers(XmlDocument collection, List<XmlElement> matches, int startIndex, int count)
    {
        var root = collection.DocumentElement!;
        var page = matches.Skip(startIndex).Take(count).ToHashSet();
        foreach (var member in Members(root).ToList())
            if (!page.Contains(member))
                root.RemoveChild(member);
        root.SetAttribute("numberReturned", page.Count.ToString(CultureInfo.InvariantCulture));
        root.SetAttribute("numberMatched", matches.Count.ToString(CultureInfo.InvariantCulture));

        using var ms = new MemoryStream();
        using (var xw = XmlWriter.Create(ms, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) }))
            collection.Save(xw);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static IEnumerable<XmlElement> Members(XmlElement root) =>
        root.ChildNodes.OfType<XmlElement>().Where(m => m.LocalName == "member" && m.NamespaceURI == OgcNamespaces.Wfs);

    private static XmlElement? FeatureOf(XmlElement member) => member.ChildNodes.OfType<XmlElement>().FirstOrDefault();

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
            if (double.TryParse(a, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var da)
                && double.TryParse(b, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var db))
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
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
                && double.TryParse(lo, NumberStyles.Float, CultureInfo.InvariantCulture, out var dlo)
                && double.TryParse(hi, NumberStyles.Float, CultureInfo.InvariantCulture, out var dhi))
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

        // Simple property names: the last step, without prefix or predicate (FesSqlTranslator)
        var step = path.Split('/').Last();
        var local = FesSqlTranslator.PropertyName(path);

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
