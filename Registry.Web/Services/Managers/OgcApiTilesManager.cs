using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using Registry.Web.Services.Caching;

namespace Registry.Web.Services.Managers;

/// <summary>
/// OGC API – Tiles manager: thin redirect/proxy onto MVT (vector) or XYZ (raster).
/// </summary>
public class OgcApiTilesManager : OgcManagerBase, IOgcApiTilesManager
{
    private readonly IOgcRasterTileSource _rasterTiles;

    public OgcApiTilesManager(OgcManagerDependencies dependencies, IOgcRasterTileSource rasterTiles)
        : base(dependencies)
    {
        _rasterTiles = rasterTiles;
    }

    public async Task<OgcApiTileSetsDto> GetTileSetsAsync(string orgSlug, string dsSlug, string collectionId, string baseUrl,
        CancellationToken ct = default)
    {
        var name = Uri.UnescapeDataString(collectionId);
        await ResolveAsync(orgSlug, dsSlug, ct);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, ct: ct)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404);

        var b = baseUrl.TrimEnd('/');
        return new OgcApiTileSetsDto(
        [
            new OgcApiTileSetDto("http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad",
            [
                new OgcApiTileLinkDto($"{b}/collections/{collectionId}/tiles/WebMercatorQuad/{{z}}/{{y}}/{{x}}", "item",
                    layer.EntryType == EntryType.Vector ? "application/vnd.mapbox-vector-tile" : "image/png")
            ])
        ]);
    }

    public async Task<byte[]?> GetTileAsync(string orgSlug, string dsSlug, string collectionId,
        string tileMatrixSet, int z, int x, int y, CancellationToken ct = default)
    {
        var name = Uri.UnescapeDataString(collectionId);
        var (_, ddb) = await ResolveAsync(orgSlug, dsSlug, ct);
        var layer = await LayerCatalog.ResolveAsync(orgSlug, dsSlug, name, ct: ct)
                    ?? throw new OgcException("InvalidParameterValue",
                        $"Unknown collection '{collectionId}'", 404);

        if (layer.EntryType == EntryType.Vector)
        {
            var path = MvtTile(ddb, layer, z, x, y);
            return path != null ? await File.ReadAllBytesAsync(path, ct) : null;
        }
        return await _rasterTiles.GetTileAsync(orgSlug, dsSlug, ddb, layer, z, x, y, "png", ct);
    }
}
