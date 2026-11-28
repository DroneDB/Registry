using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Registry.Ports.DroneDB;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers.Wfs;

/// <summary>The WFS documents that describe the service (out of <c>WfsManager</c>): capabilities and the stored queries.</summary>
public class WfsCapabilities : OgcManagerBase
{
    public WfsCapabilities(OgcManagerDependencies dependencies)
        : base(dependencies)
    {
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

    public async Task<string> GetCapabilitiesAsync(string orgSlug, string dsSlug, string? folderPath = null,
        CancellationToken ct = default)
    {
        var key = await LayerCatalog.CacheKeyAsync(orgSlug, dsSlug, "caps-wfs-v2-2.0.0", folderPath, ct);
        return await Records.GetOrCreateRecordAsync(key, async token =>
        {
            await ResolveAsync(orgSlug, dsSlug, token);
            var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, folderPath, ct: token);
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
                    [.. WfsConformance.GmlFormats]);
                await WriteWfsOperationAsync(w, "GetFeature", baseUrl, [.. WfsConformance.FeatureFormats]);
                await WriteWfsOperationAsync(w, "GetPropertyValue", baseUrl, [.. WfsConformance.GmlFormats]);
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
            return xml;
        }, CapabilitiesCache, ct);
    }

    public async Task<string> ListStoredQueriesAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, ct: ct);
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

    public async Task<string> DescribeStoredQueriesAsync(string orgSlug, string dsSlug, string[] storedQueryIds,
        CancellationToken ct = default)
    {
        var layers = await LayerCatalog.GetLayersAsync(orgSlug, dsSlug, ct: ct);
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
}
