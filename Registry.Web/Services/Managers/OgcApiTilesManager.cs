#nullable disable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Ogc;

namespace Registry.Web.Services.Managers;

/// <summary>
/// OGC API – Tiles manager: thin redirect/proxy onto MVT (vector) or XYZ (raster).
/// </summary>
public class OgcApiTilesManager : OgcManagerBase
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
            new OgcApiTileSetDto(OgcApiTilesConformance.WebMercatorQuadUri,
            [
                new OgcApiTileLinkDto($"{b}/collections/{collectionId}/tiles/{OgcApiTilesConformance.WebMercatorQuad}/{{z}}/{{y}}/{{x}}", "item",
                    layer.EntryType == EntryType.Vector ? "application/vnd.mapbox-vector-tile" : "image/png")
            ])
        ]);
    }

    public async Task<byte[]?> GetTileAsync(string orgSlug, string dsSlug, string collectionId,
        string tileMatrixSet, int z, int x, int y, CancellationToken ct = default)
    {
        OgcApiTilesConformance.Validate(tileMatrixSet);
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
