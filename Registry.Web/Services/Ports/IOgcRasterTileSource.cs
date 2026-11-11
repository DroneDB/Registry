using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Raster tiles of OGC layers (WMTS GetTile, OGC API Tiles), served from the tile cache of the
/// Registry tile endpoint.
/// </summary>
public interface IOgcRasterTileSource
{
    /// <summary>
    /// The 256 px tile z/x/y of a raster layer encoded as <paramref name="outputFormat"/>
    /// ("png" or "jpeg"), rendered once per cache lifetime from <paramref name="cogPath"/>.
    /// </summary>
    /// <param name="cogPath">The COG of the layer's build the caller resolved: the tile never reads the entry's own
    /// file, which is what a user uploaded.</param>
    Task<byte[]> GetTileAsync(string orgSlug, string dsSlug, IDDB ddb, OgcLayerDto layer, string cogPath,
        int z, int x, int y, string outputFormat, CancellationToken ct = default);
}
