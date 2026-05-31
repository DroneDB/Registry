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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>OGC API – Tiles manager: thin redirect/proxy onto MVT (vector) or XYZ (raster).</summary>
public class OgcApiTilesManager : OgcManagerBase, IOgcApiTilesManager
{
    public OgcApiTilesManager(IUtils u, IAuthManager a, IDdbManager d, IBuildArtifactResolver ar,
        IDdbWrapper w, IOgcLayerCatalog c, IDistributedCache cache, IHttpContextAccessor ctx)
        : base(u, a, d, ar, w, c, cache, ctx) { }

    public async Task<object> GetTileSetsAsync(string orgSlug, string dsSlug, string collectionId, string baseUrl)
    {
        var name = Uri.UnescapeDataString(collectionId);
        await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404);

        var b = baseUrl.TrimEnd('/');
        return new
        {
            tilesets = new[]
            {
                new
                {
                    tileMatrixSetURI = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad",
                    links = new[]
                    {
                        new { href = $"{b}/collections/{collectionId}/tiles/WebMercatorQuad/{{z}}/{{y}}/{{x}}",
                              rel = "item",
                              type = layer.EntryType == EntryType.Vector
                                  ? "application/vnd.mapbox-vector-tile"
                                  : "image/png" }
                    }
                }
            }
        };
    }

    public async Task<byte[]?> GetTileAsync(string orgSlug, string dsSlug, string collectionId,
        string tileMatrixSet, int z, int x, int y)
    {
        var name = Uri.UnescapeDataString(collectionId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404);

        if (layer.EntryType == EntryType.Vector)
        {
            var path = Artifacts.GetMvtTilePath(ddb, layer.EntryHash, z, x, y);
            return Artifacts.ArtifactExists(path) ? await File.ReadAllBytesAsync(path) : null;
        }
        return ddb.GenerateTile(layer.EntryPath, z, x, y, retina: false, inputPathHash: layer.EntryHash);
    }
}
