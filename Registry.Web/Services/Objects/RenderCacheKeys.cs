using System;
using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The key parts of every render the server caches (thumbnails, plain and visualized tiles), each built in one place
/// so that the read, the removal and any pre-generation of an entry agree. Each key starts with the hash of the entry
/// and the version of the file that is rendered (<see cref="Utilities.FileVersion"/>): a build rewrites its artifact
/// under the same hash, and a render of the old file must not be served for the new one.
/// </summary>
public static class RenderCacheKeys
{
    /// <summary>The plain thumbnail of <paramref name="pixels"/> px.</summary>
    public static object[] Thumbnail(string hash, long version, int pixels) => [hash, version, pixels];

    /// <summary>The thumbnail of <paramref name="pixels"/> px with the visualization <paramref name="visualization"/>.</summary>
    public static object[] ThumbnailEx(string hash, long version, int pixels, string visualization) =>
        [hash, version, pixels, visualization];

    /// <summary>The plain tile (as the URL numbers it) in <paramref name="mediaType"/>.</summary>
    public static object[] Tile(string hash, long version, int x, int y, int z, bool retina, string mediaType) =>
        [hash, version, x, y, z, retina, mediaType];

    /// <summary>The tile with the visualization <paramref name="visualization"/> in <paramref name="mediaType"/>.</summary>
    public static object[] TileEx(string hash, long version, int x, int y, int z, bool retina, string visualization,
        string mediaType) =>
        [hash, version, x, y, z, retina, visualization, mediaType];

    /// <summary>
    /// The parameters of a cache read: the <paramref name="key"/> followed by the render that makes the entry on a miss
    /// (the cache leaves a delegate out of the key).
    /// </summary>
    public static object[] WithRender(this object[] key, Func<CancellationToken, Task<byte[]>> render)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(render);
        return [.. key, render];
    }
}
