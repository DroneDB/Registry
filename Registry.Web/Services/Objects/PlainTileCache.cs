using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The one cache entry of a plain tile (no visualization) of a raster, shared by every endpoint that serves it: the
/// tile endpoint of the Registry (<c>tiles/{z}/{x}/{y}</c>) and the OGC layers (WMTS GetTile, OGC API Tiles). The key
/// is the content hash of the entry and the version of the file the tile is read from (<see cref="RenderCacheKeys"/>:
/// a rebuilt COG has the same hash), the tile (x, y, z, as the URL numbers its row: every endpoint passes it to libddb
/// unchanged), whether it is a retina one and the media type, so the same tile asked through two endpoints is rendered
/// and stored once. The render runs on the tiles lane with the cache flight's token: it stops when every request for
/// the tile has gone.
/// </summary>
public static class PlainTileCache
{
    /// <summary>
    /// The tile z/x/y of <paramref name="sourcePath"/> (the file the entry's tiles read, its COG for a raster) in
    /// <paramref name="format"/>, from the cache or rendered once. <paramref name="sourceVersion"/> is the version of
    /// <paramref name="sourcePath"/> (<see cref="Utilities.FileVersion"/>).
    /// </summary>
    public static Task<byte[]> GetAsync(ICacheManager cache, INativeWorkGate gate, string orgSlug, string dsSlug,
        IDDB ddb, string sourcePath, string entryHash, long sourceVersion, int z, int x, int y, bool retina,
        TileFormat format, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(ddb);
        ArgumentNullException.ThrowIfNull(format);

        return cache.GetAsync(MagicStrings.TileCacheSeed, ForDataset(orgSlug, dsSlug), ct,
            RenderCacheKeys.Tile(entryHash, sourceVersion, x, y, z, retina, format.MediaType).WithRender(token =>
                gate.RunAsync(NativeLane.Tiles,
                    () => ddb.GenerateTile(sourcePath, z, x, y, retina, entryHash, format.DdbName), token)));
    }
}
