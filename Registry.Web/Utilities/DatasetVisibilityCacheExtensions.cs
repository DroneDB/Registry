#nullable enable
using System;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Utilities;

public static class DatasetVisibilityCacheExtensions
{
    /// <summary>
    /// The visibility of a dataset from the <see cref="MagicStrings.DatasetVisibilityCacheSeed"/>
    /// cache (read from the index meta on a miss). Null when the cached value is invalid.
    /// </summary>
    public static async Task<Visibility?> GetDatasetVisibilityAsync(this ICacheManager cacheManager,
        string orgSlug, Guid internalRef, IDdbManager ddbManager)
    {
        var visibilityBytes = await cacheManager.GetAsync(
            MagicStrings.DatasetVisibilityCacheSeed,
            orgSlug,
            orgSlug,
            internalRef,
            ddbManager
        );

        return visibilityBytes.Length < sizeof(int) ? null : (Visibility)BitConverter.ToInt32(visibilityBytes, 0);
    }
}
