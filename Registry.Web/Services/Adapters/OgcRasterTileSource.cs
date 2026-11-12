using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Serves OGC raster tiles through the cache of the Registry tile endpoint (<see cref="PlainTileCache"/>, dataset
/// category): concurrent requests for a tile render it once, entries expire with the other tiles and a dataset
/// invalidation clears them. A tile asked through an OGC service and through the tile endpoint is one entry: the
/// same hash, tile, retina flag (false: OGC tiles are 256 px) and media type.
/// </summary>
public class OgcRasterTileSource : IOgcRasterTileSource
{
    private readonly ICacheManager _cacheManager;
    private readonly INativeWorkGate _gate;

    public OgcRasterTileSource(ICacheManager cacheManager, INativeWorkGate? gate = null)
    {
        _cacheManager = cacheManager;
        _gate = gate ?? NativeWorkGate.Default;
    }

    public Task<byte[]> GetTileAsync(string orgSlug, string dsSlug, IDDB ddb, OgcLayerDto layer, string cogPath,
        int z, int x, int y, TileFormat format, CancellationToken ct = default) =>
        PlainTileCache.GetAsync(_cacheManager, _gate, orgSlug, dsSlug, ddb, cogPath, layer.EntryHash, z, x, y,
            retina: false, format, ct);
}
