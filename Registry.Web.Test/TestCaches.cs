#nullable enable
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Registry.Ports;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Test;

/// <summary>
/// Caches for tests that build managers by hand.
/// </summary>
public static class TestCaches
{
    /// <summary>A <see cref="DatasetSummaryProvider"/> with its own memory cache.</summary>
    public static IDatasetSummaryProvider Summaries(IDdbManager ddbManager) =>
        new DatasetSummaryProvider(new MemoryCache(new MemoryCacheOptions()), ddbManager);

    /// <summary>A <see cref="CacheManager"/> over <paramref name="store"/> with the dataset visibility seed.</summary>
    public static ICacheManager WithDatasetVisibility(IDistributedCache store)
    {
        var cacheManager = new CacheManager(store, NullLogger<CacheManager>.Instance, new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        cacheManager.Register(MagicStrings.DatasetVisibilityCacheSeed,
            CacheProviderFactories.CreateDatasetVisibilityProvider());
        return cacheManager;
    }
}
