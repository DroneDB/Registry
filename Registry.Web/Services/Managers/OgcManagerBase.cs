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
    protected readonly IUtils Utils;
    protected readonly IAuthManager AuthManager;
    protected readonly IDdbManager DdbManager;
    protected readonly IBuildArtifactResolver Artifacts;
    protected readonly IDdbWrapper DdbWrapper;
    protected readonly IOgcLayerCatalog LayerCatalog;
    protected readonly IDistributedCache Cache;
    protected static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

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
