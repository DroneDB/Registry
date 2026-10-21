using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Registry.Common.Model;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Helpers shared by every OGC manager: dataset resolution, auth, capabilities caching.
/// </summary>
public abstract class OgcManagerBase
{
    // OGC / W3C namespace URIs (single source of truth: OgcNamespaces)
    protected const string NsWms   = OgcNamespaces.Wms;
    protected const string NsWfs   = OgcNamespaces.Wfs;
    protected const string NsWmts  = OgcNamespaces.Wmts;
    protected const string NsOws   = OgcNamespaces.Ows;
    protected const string NsGml   = OgcNamespaces.Gml;
    protected const string NsXlink = OgcNamespaces.Xlink;
    protected const string NsXsd   = OgcNamespaces.Xsd;
    protected const string NsDdb   = OgcNamespaces.Ddb;

    protected readonly IDatasetAccessResolver Access;
    protected readonly IBuildArtifactResolver Artifacts;
    protected readonly IDdbWrapper DdbWrapper;
    protected readonly IOgcLayerCatalog LayerCatalog;
    protected readonly IDistributedCache Cache;
    protected readonly IHttpContextAccessor HttpContext;
    protected static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>XML declaration with explicit UTF-8 (matches the actual wire encoding produced by .NET strings).</summary>
    protected const string Utf8XmlDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

    /// <summary>Creates a reusable indented <see cref="XmlWriter"/> over the given <paramref name="sb"/>.
    /// The XML declaration is omitted; callers must prepend <see cref="Utf8XmlDecl"/> so the wire
    /// XML correctly reports utf-8 (StringBuilder is UTF-16 internally).</summary>
    protected static XmlWriter CreateXmlWriter(StringBuilder sb) =>
        XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true });

    protected OgcManagerBase(IDatasetAccessResolver access,
        IBuildArtifactResolver artifacts, IDdbWrapper ddbWrapper, IOgcLayerCatalog catalog,
        IDistributedCache cache, IHttpContextAccessor httpContext)
    {
        Access = access;
        Artifacts = artifacts; DdbWrapper = ddbWrapper; LayerCatalog = catalog; Cache = cache;
        HttpContext = httpContext;
    }

    /// <summary>
    /// Builds the absolute base URL for the current OGC service endpoint, e.g.
    /// "http://host:port/orgs/admin/ds/test-ort/wms". Falls back to a fabricated
    /// localhost URL if there is no ambient HTTP context (e.g. unit tests).
    /// The returned string never ends with '?' - callers append their own query string.
    /// </summary>
    protected string GetServiceUrl(string orgSlug, string dsSlug, string service, string? folderPath = null)
    {
        var ctx = HttpContext?.HttpContext;
        string baseUrl;
        if (ctx != null)
        {
            var req = ctx.Request;
            var pathBase = req.PathBase.HasValue ? req.PathBase.Value : string.Empty;
            baseUrl = $"{req.Scheme}://{req.Host}{pathBase}";
        }
        else
        {
            baseUrl = "http://localhost";
        }
        var path = $"/orgs/{orgSlug}/ds/{dsSlug}/{service}";
        if (!string.IsNullOrEmpty(folderPath))
            path += "/p/" + folderPath.TrimStart('/');
        return baseUrl + path;
    }

    protected async Task<(Registry.Web.Data.Models.Dataset ds, IDDB ddb)> ResolveAsync(string orgSlug, string dsSlug)
    {
        // OgcAuthorizationFilter (applied at controller level) granted read access to this request's
        // dataset through the same scoped resolver, which reuses the grant. A manager invoked outside an
        // authorized OGC request is checked here.
        var access = await Access.ResolveAsync(orgSlug, dsSlug, AccessType.Read);
        return (access.Dataset, access.Ddb);
    }

    protected static double[]? AggregateBbox(IReadOnlyList<OgcLayerDto> layers)
    {
        double minLon = 180, minLat = 90, maxLon = -180, maxLat = -90;
        var any = false;
        foreach (var l in layers)
        {
            if (l.BboxWgs84 == null || l.BboxWgs84.Length < 4) continue;
            any = true;
            if (l.BboxWgs84[0] < minLon) minLon = l.BboxWgs84[0];
            if (l.BboxWgs84[1] < minLat) minLat = l.BboxWgs84[1];
            if (l.BboxWgs84[2] > maxLon) maxLon = l.BboxWgs84[2];
            if (l.BboxWgs84[3] > maxLat) maxLat = l.BboxWgs84[3];
        }
        return any ? [minLon, minLat, maxLon, maxLat] : null;
    }

    protected string ResolveRasterArtifact(IDDB ddb, OgcLayerDto layer)
    {
        var cog = Artifacts.GetCogPath(ddb, layer.EntryHash);
        return Artifacts.ArtifactExists(cog) ? cog :
            // Fallback to source file via ddb local path.
            ddb.GetLocalPath(layer.EntryPath);
    }

    protected string ResolveVectorArtifact(IDDB ddb, OgcLayerDto layer)
    {
        var gpkg = Artifacts.GetVectorQueryPath(ddb, layer.EntryHash);
        if (!Artifacts.ArtifactExists(gpkg))
            // OWS Common 2.0: a referenced layer/typeName the server cannot serve is
            // an invalid parameter value, not an unsupported operation. Returning 404
            // here would violate the WFS 2.0 ATS (typeNames is a parameter, not a resource).
            throw new OgcException("InvalidParameterValue",
                $"Vector layer '{layer.Name}' has no built GPKG sidecar. Run dataset build first.",
                400, "typeNames");
        return gpkg;
    }

    /// <summary>
    /// Emits a WMS <c>&lt;DCPType&gt;&lt;HTTP&gt;&lt;Get&gt;&lt;OnlineResource ... /&gt;&lt;/Get&gt;&lt;/HTTP&gt;&lt;/DCPType&gt;</c> block.
    /// The base URL must end without a query string; ETS clients append params.
    /// </summary>
    protected static void WriteWmsDcp(XmlWriter w, string baseUrl)
    {
        w.WriteStartElement("DCPType");
        w.WriteStartElement("HTTP");
        w.WriteStartElement("Get");
        w.WriteStartElement("OnlineResource");
        w.WriteAttributeString("xmlns", "xlink", null, NsXlink);
        w.WriteAttributeString("xlink", "type", NsXlink, "simple");
        w.WriteAttributeString("xlink", "href", NsXlink, baseUrl + "?");
        w.WriteEndElement(); // OnlineResource
        w.WriteEndElement(); // Get
        w.WriteEndElement(); // HTTP
        w.WriteEndElement(); // DCPType
    }

    /// <summary>
    /// Emits an OWS <c>&lt;ows:Operation name="..."&gt;</c> entry with HTTP Get binding pointing at <paramref name="baseUrl"/>.
    /// </summary>
    protected static async Task WriteOwsOperationAsync(XmlWriter w, string opName, string baseUrl,
        bool addGetEncodingKvpConstraint = false)
    {
        await w.WriteStartElementAsync("ows", "Operation", null);
        w.WriteAttributeString("name", opName);
        await w.WriteStartElementAsync("ows", "DCP", null);
        await w.WriteStartElementAsync("ows", "HTTP", null);
        await w.WriteStartElementAsync("ows", "Get", null);
        await w.WriteAttributeStringAsync("xlink", "href", NsXlink, baseUrl + "?");
        if (addGetEncodingKvpConstraint)
        {
            // WMTS 1.0 CITE requires <ows:Constraint name="GetEncoding"> with AllowedValues=KVP
            // on every <ows:Get> operation to advertise the KVP binding (mandatory test
            // Server.KVP.GET.GetCapabilities.Response.Encoding.Constraint).
            await w.WriteStartElementAsync("ows", "Constraint", null);
            w.WriteAttributeString("name", "GetEncoding");
            await w.WriteStartElementAsync("ows", "AllowedValues", null);
            await w.WriteStartElementAsync("ows", "Value", null);
            await w.WriteStringAsync("KVP");
            await w.WriteEndElementAsync(); // Value
            await w.WriteEndElementAsync(); // AllowedValues
            await w.WriteEndElementAsync(); // Constraint
        }
        await w.WriteEndElementAsync(); // Get
        await w.WriteEndElementAsync(); // HTTP
        await w.WriteEndElementAsync(); // DCP
        await w.WriteEndElementAsync(); // Operation
    }
}
