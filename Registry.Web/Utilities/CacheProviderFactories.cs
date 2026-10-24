using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Utilities;

/// <summary>
/// Provides factory methods for creating cache provider functions.
/// Centralizes cache provider logic to avoid duplication between production and test code.
/// </summary>
public static class CacheProviderFactories
{
    /// <summary>
    /// Creates a cache provider function that stores what the caller generates.
    /// </summary>
    /// <returns>
    /// A function that takes parameters (key parts..., generate) and returns the bytes of generate as they are.
    /// generate (<c>Func&lt;CancellationToken, Task&lt;byte[]&gt;&gt;</c>, always the last parameter) gets the
    /// generation's token, canceled once no caller waits for it (RQ-13).
    /// </returns>
    public static Func<object[], CancellationToken, Task<byte[]>> CreatePassThroughProvider() =>
        (parameters, ct) => ((Func<CancellationToken, Task<byte[]>>)parameters[^1])(ct);

    /// <summary>
    /// Creates a cache provider function for tiles: parameters (fileHash, tx, ty, tz, retina,
    /// [vizKey | format], generate). Each producer encodes its tiles in the format it serves
    /// (the tile endpoint gets WebP from the native tiler, WMTS PNG or JPEG), so the bytes are
    /// stored as they are.
    /// </summary>
    public static Func<object[], CancellationToken, Task<byte[]>> CreateTileProvider() => CreatePassThroughProvider();

    /// <summary>
    /// Creates a cache provider function for thumbnails: parameters (fileHash, size, [vizKey], generate), stored as
    /// they are. A failure is logged once, by the cache.
    /// </summary>
    public static Func<object[], CancellationToken, Task<byte[]>> CreateThumbnailProvider() => CreatePassThroughProvider();

    /// <summary>
    /// Creates a cache provider function for dataset visibility.
    /// </summary>
    /// <returns>
    /// A function that takes parameters (orgSlug, internalRef, ddbManager)
    /// and returns the dataset visibility as a byte array.
    /// </returns>
    public static Func<object[], Task<byte[]>> CreateDatasetVisibilityProvider()
    {
        return parameters =>
        {
            // Parameters: orgSlug, internalRef, ddbManager
            var orgSlug = (string)parameters[0];
            var internalRef = (Guid)parameters[1];
            var ddbManager = (IDdbManager)parameters[2];

            var ddb = ddbManager.Get(orgSlug, internalRef);
            var meta = ddb.Meta.GetSafe();
            var visibility = (int)(meta.Visibility ?? Visibility.Private);

            return Task.FromResult(BitConverter.GetBytes(visibility));
        };
    }

    /// <summary>
    /// A cache provider function for a dataset's display name (its root "name" meta): parameters (orgSlug,
    /// internalRef, ddbManager), the name as UTF-8, empty when the dataset has none.
    /// </summary>
    public static Func<object[], Task<byte[]>> CreateDatasetNameProvider()
    {
        return parameters =>
        {
            var orgSlug = (string)parameters[0];
            var internalRef = (Guid)parameters[1];
            var ddbManager = (IDdbManager)parameters[2];

            var name = ddbManager.Get(orgSlug, internalRef).Meta.GetSafe().Name;
            return Task.FromResult(name == null ? [] : System.Text.Encoding.UTF8.GetBytes(name));
        };
    }
}
