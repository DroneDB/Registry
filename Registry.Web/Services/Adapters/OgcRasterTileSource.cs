using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Serves OGC raster tiles through the cache of the Registry tile endpoint
/// (<see cref="MagicStrings.TileCacheSeed"/>, dataset category): concurrent requests for a tile
/// render it once, entries expire with the other tiles and a dataset invalidation clears them.
/// The format is part of the key: the tile endpoint caches one entry per format too.
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

    public Task<byte[]> GetTileAsync(string orgSlug, string dsSlug, IDDB ddb, OgcLayerDto layer,
        int z, int x, int y, string outputFormat, CancellationToken ct = default)
    {
        // The render runs with the cache flight's token: it stops when every request for the tile has gone
        return _cacheManager.GetAsync(MagicStrings.TileCacheSeed, CacheCategories.ForDataset(orgSlug, dsSlug), ct,
            layer.EntryHash, x, y, z, false, outputFormat,
            new Func<CancellationToken, Task<byte[]>>(token => _gate.RunAsync(NativeLane.Tiles, () => ddb.GenerateTile(
                layer.EntryPath, z, x, y, retina: false, inputPathHash: layer.EntryHash, outputFormat: outputFormat),
                token)));
    }
}
