#nullable enable
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Registry.Ports;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Test;

/// <summary>
/// Caches for tests that build managers by hand.
/// </summary>
public static class TestCaches
{
    /// <summary>A new in-memory distributed cache.</summary>
    public static MemoryDistributedCache MemoryStore() =>
        new(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    /// <summary>The record cache over <paramref name="store"/> (a new in-memory store when null).</summary>
    public static RecordCache Records(IDistributedCache? store = null) =>
        new(store ?? MemoryStore(), NullLogger<RecordCache>.Instance);

    /// <summary>The cache generations over <paramref name="store"/> (a new in-memory store when null).</summary>
    public static CacheGenerations Generations(IDistributedCache? store = null) =>
        new(store ?? MemoryStore(), NullLogger<CacheGenerations>.Instance);

    /// <summary>The OGC cache keys over <paramref name="store"/> (a new in-memory store when null).</summary>
    public static OgcCacheKeys OgcKeys(IDistributedCache? store = null) => new(Generations(store));

    /// <summary>A <see cref="DatasetSummaryProvider"/> with its own memory cache.</summary>
    public static IDatasetSummaryProvider Summaries(IDdbManager ddbManager) =>
        new DatasetSummaryProvider(new MemoryCache(new MemoryCacheOptions()), ddbManager);

    /// <summary>A <see cref="CacheManager"/> over a new in-memory store with the dataset visibility seed.</summary>
    public static ICacheManager InMemoryWithDatasetVisibility() =>
        WithDatasetVisibility(new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

    /// <summary>A <see cref="CacheManager"/> over <paramref name="store"/> with the dataset visibility and name seeds.</summary>
    public static ICacheManager WithDatasetVisibility(IDistributedCache store)
    {
        var cacheManager = new CacheManager(store, NullLogger<CacheManager>.Instance, new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        cacheManager.Register(MagicStrings.DatasetVisibilityCacheSeed,
            CacheProviderFactories.CreateDatasetVisibilityProvider());
        cacheManager.Register(MagicStrings.DatasetNameCacheSeed, CacheProviderFactories.CreateDatasetNameProvider());
        return cacheManager;
    }
}
