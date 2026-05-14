using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
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
    // OGC / W3C namespace URIs — single source of truth for all derived managers.
    protected const string NsWms   = "http://www.opengis.net/wms";
    protected const string NsWfs   = "http://www.opengis.net/wfs/2.0";
    protected const string NsWmts  = "http://www.opengis.net/wmts/1.0";
    protected const string NsOws   = "http://www.opengis.net/ows/1.1";
    protected const string NsGml   = "http://www.opengis.net/gml/3.2";
    protected const string NsXlink = "http://www.w3.org/1999/xlink";
    protected const string NsXsd   = "http://www.w3.org/2001/XMLSchema";

    protected readonly IUtils Utils;
    protected readonly IAuthManager AuthManager;
    protected readonly IDdbManager DdbManager;
    protected readonly IBuildArtifactResolver Artifacts;
    protected readonly IDdbWrapper DdbWrapper;
    protected readonly IOgcLayerCatalog LayerCatalog;
    protected readonly IDistributedCache Cache;
    protected static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>XML declaration with explicit UTF-8 (matches the actual wire encoding produced by .NET strings).</summary>
    protected const string Utf8XmlDecl = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

    /// <summary>Creates a reusable indented <see cref="XmlWriter"/> over the given <paramref name="sb"/>.
    /// The XML declaration is omitted; callers must prepend <see cref="Utf8XmlDecl"/> so the wire
    /// XML correctly reports utf-8 (StringBuilder is UTF-16 internally).</summary>
    protected static XmlWriter CreateXmlWriter(StringBuilder sb) =>
        XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Async = true, OmitXmlDeclaration = true });

    protected OgcManagerBase(IUtils utils, IAuthManager authManager, IDdbManager ddbManager,
        IBuildArtifactResolver artifacts, IDdbWrapper ddbWrapper, IOgcLayerCatalog catalog,
        IDistributedCache cache)
    {
        Utils = utils; AuthManager = authManager; DdbManager = ddbManager;
        Artifacts = artifacts; DdbWrapper = ddbWrapper; LayerCatalog = catalog; Cache = cache;
    }

    protected async Task<(Registry.Web.Data.Models.Dataset ds, IDDB ddb)> ResolveAsync(string orgSlug, string dsSlug)
    {
        var ds = Utils.GetDataset(orgSlug, dsSlug);
        if (!await AuthManager.RequestAccess(ds, AccessType.Read))
            throw new UnauthorizedException("Read access denied");
        var ddb = DdbManager.Get(orgSlug, ds.InternalRef);
        return (ds, ddb);
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
        return any ? new[] { minLon, minLat, maxLon, maxLat } : null;
    }

    protected string ResolveRasterArtifact(IDDB ddb, OgcLayerDto layer)
    {
        var cog = Artifacts.GetCogPath(ddb, layer.EntryHash);
        if (Artifacts.ArtifactExists(cog)) return cog;
        // Fallback to source file via ddb local path.
        return ddb.GetLocalPath(layer.EntryPath);
    }

    protected string ResolveVectorArtifact(IDDB ddb, OgcLayerDto layer)
    {
        var gpkg = Artifacts.GetVectorQueryPath(ddb, layer.EntryHash);
        if (!Artifacts.ArtifactExists(gpkg))
            throw new OgcException("OperationNotSupported",
                $"Vector layer '{layer.Name}' has no built GPKG sidecar. Run dataset build first.",
                404, "LAYERS");
        return gpkg;
    }
}
