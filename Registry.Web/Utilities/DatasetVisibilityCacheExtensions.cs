using System;
using System.Threading;
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

    /// <summary>
    /// Drops the cached visibility of a dataset (the next read asks the index). Through
    /// <see cref="Services.Caching.IDatasetListingInvalidator"/>, which also drops the listings that depend on it.
    /// </summary>
    public static Task RemoveDatasetVisibilityAsync(this ICacheManager cacheManager, string orgSlug, Guid internalRef,
        IDdbManager ddbManager) =>
        cacheManager.RemoveAsync(MagicStrings.DatasetVisibilityCacheSeed, orgSlug, orgSlug, internalRef, ddbManager);

    /// <summary>
    /// The display name of a dataset from the <see cref="MagicStrings.DatasetNameCacheSeed"/> cache (read from the
    /// index meta on a miss), null when it has none. Kept in process memory like the visibility, so a listing of
    /// many datasets makes no cache round trip per dataset once warm.
    /// </summary>
    public static async Task<string?> GetDatasetNameAsync(this ICacheManager cacheManager, string orgSlug,
        Guid internalRef, IDdbManager ddbManager, CancellationToken ct = default)
    {
        var name = await cacheManager.GetAsync(MagicStrings.DatasetNameCacheSeed, orgSlug, ct, orgSlug, internalRef,
            ddbManager);
        return name.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(name);
    }

    /// <summary>Drops the cached display name of a dataset.</summary>
    public static Task RemoveDatasetNameAsync(this ICacheManager cacheManager, string orgSlug, Guid internalRef,
        IDdbManager ddbManager) =>
        cacheManager.RemoveAsync(MagicStrings.DatasetNameCacheSeed, orgSlug, orgSlug, internalRef, ddbManager);
}
