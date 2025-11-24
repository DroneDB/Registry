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
    /// Creates a cache provider function for tiles.
    /// </summary>
    /// <returns>
    /// A function that takes parameters (fileHash, tx, ty, tz, retina, generateFunc)
    /// and returns the tile data converted to WebP format.
    /// </returns>
    public static Func<object[], Task<byte[]>> CreateTileProvider()
    {
        return async parameters =>
        {
            // Parameters: fileHash, tx, ty, tz, retina, generateFunc
            var generateFunc = (Func<Task<byte[]>>)parameters[5];
            var data = await generateFunc();
            return data.ToWebp(90);
        };
    }

    /// <summary>
    /// Creates a cache provider function for thumbnails.
    /// </summary>
    /// <returns>
    /// A function that takes parameters (fileHash, size, generateFunc)
    /// and returns the thumbnail data.
    /// </returns>
    public static Func<object[], Task<byte[]>> CreateThumbnailProvider()
    {
        return async parameters =>
        {
            try
            {
                // Parameters: fileHash, size, generateFunc
                var generateFunc = (Func<Task<byte[]>>)parameters[2];
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
        return async parameters =>
        {
            // Parameters: orgSlug, internalRef, ddbManager
            var orgSlug = (string)parameters[0];
            var internalRef = (Guid)parameters[1];
            var ddbManager = (IDdbManager)parameters[2];

            var ddb = ddbManager.Get(orgSlug, internalRef);
            var meta = ddb.Meta.GetSafe();
            var visibility = (int)(meta.Visibility ?? Visibility.Private);

            return BitConverter.GetBytes(visibility);
        };
    }
}
