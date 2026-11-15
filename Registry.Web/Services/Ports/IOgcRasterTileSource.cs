using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Objects;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Raster tiles of OGC layers (WMTS GetTile, OGC API Tiles), served from the tile cache of the
/// Registry tile endpoint.
/// </summary>
public interface IOgcRasterTileSource
{
    /// <summary>
    /// The 256 px tile z/x/y of a raster layer encoded as <paramref name="format"/>, rendered once per cache
    /// lifetime from <paramref name="cogPath"/>: the entry the tile endpoint of the Registry serves the same tile from.
    /// </summary>
    /// <param name="cogPath">The COG of the layer's build the caller resolved: the tile never reads the entry's own
    /// file, which is what a user uploaded.</param>
    Task<byte[]> GetTileAsync(string orgSlug, string dsSlug, IDDB ddb, OgcLayerDto layer, string cogPath,
        int z, int x, int y, TileFormat format, CancellationToken ct = default);
}
