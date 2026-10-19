using System;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Serilog;

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
    /// A function that takes parameters (key parts..., generateFunc) and returns the bytes of
    /// generateFunc as they are.
    /// </returns>
    public static Func<object[], Task<byte[]>> CreatePassThroughProvider()
    {
        return parameters =>
        {
            // The generateFunc is always the last parameter
            var generateFunc = (Func<Task<byte[]>>)parameters[^1];
            return generateFunc();
        };
    }

    /// <summary>
    /// Creates a cache provider function for tiles: parameters (fileHash, tx, ty, tz, retina,
    /// [vizKey | format], generateFunc). Each producer encodes its tiles in the format it serves
    /// (the tile endpoint gets WebP from the native tiler, WMTS PNG or JPEG), so the bytes are
    /// stored as they are.
    /// </summary>
    public static Func<object[], Task<byte[]>> CreateTileProvider() => CreatePassThroughProvider();

    /// <summary>
    /// Creates a cache provider function for thumbnails.
    /// </summary>
    /// <returns>
    /// A function that takes parameters (fileHash, size, [vizKey], generateFunc)
    /// and returns the thumbnail data.
    /// </returns>
    public static Func<object[], Task<byte[]>> CreateThumbnailProvider()
    {
        return async parameters =>
        {
            try
            {
                // The generateFunc is always the last parameter
                var generateFunc = (Func<Task<byte[]>>)parameters[^1];
                return await generateFunc();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error generating thumbnail");
                throw;
            }
        };
    }

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
}
