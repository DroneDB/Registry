using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of every web or job host (<see cref="StartupTaskOrder.Caches"/>): checks the cache
/// connection and registers the cache providers (<c>BuildPendingService</c> and the managers need them).
/// </summary>
internal sealed class CacheInitializer : IStartupTask
{
    private readonly ICacheManager _cacheManager;
    private readonly CachingOptions _caching;
    private readonly ILogger<CacheInitializer> _logger;

    public CacheInitializer(ICacheManager cacheManager, IOptions<CachingOptions> caching, ILogger<CacheInitializer> logger)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _caching = (caching ?? throw new ArgumentNullException(nameof(caching))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public int Order => StartupTaskOrder.Caches;

    public async Task ExecuteAsync(CancellationToken token)
    {
        _logger.LogInformation("Cache initialization starting");

        await ValidateCacheConnectionAsync(_caching);
        RegisterCacheProviders(_caching);

        _logger.LogInformation("Cache initialization completed");
    }

    private async Task ValidateCacheConnectionAsync(CachingOptions appSettings)
    {
        _logger.LogInformation("Validating cache connection");

        try
        {
            await StartupExtenders.ValidateCacheConnection(appSettings);
            _logger.LogInformation("Cache connection validated successfully");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Cache validation failed: {Message}", ex.Message);
            throw new InvalidOperationException($"Cache validation failed: {ex.Message}", ex);
        }
    }

    /// <summary>WMS GetMap renders are kept 10 minutes after their last use.</summary>
    private static readonly TimeSpan OgcRenderCacheExpiration = TimeSpan.FromMinutes(10);

    private void RegisterCacheProviders(CachingOptions appSettings)
    {
        _logger.LogInformation("Registering cache providers");

        var cacheManager = _cacheManager;

        // Register tile cache provider
        cacheManager.Register(
            MagicStrings.TileCacheSeed,
            CacheProviderFactories.CreateTileProvider(),
            appSettings.TilesCacheExpiration);

        _logger.LogDebug("Registered tile cache provider with expiration: {Expiration}",
            appSettings.TilesCacheExpiration?.ToString() ?? "default");

        // Register thumbnail cache provider
        cacheManager.Register(
            MagicStrings.ThumbnailCacheSeed,
            CacheProviderFactories.CreateThumbnailProvider(),
            appSettings.ThumbnailsCacheExpiration);

        _logger.LogDebug("Registered thumbnail cache provider with expiration: {Expiration}",
            appSettings.ThumbnailsCacheExpiration?.ToString() ?? "default");

        // Register the WMS render cache provider (pass-through: WmsManager encodes the image)
        cacheManager.Register(
            MagicStrings.OgcRenderCacheSeed,
            CacheProviderFactories.CreatePassThroughProvider(),
            OgcRenderCacheExpiration);

        _logger.LogDebug("Registered OGC render cache provider with expiration: {Expiration}", OgcRenderCacheExpiration);

        // Register dataset visibility cache provider
        // Read on every request to a dataset (each COPC range request of a point cloud): kept in
        // memory as long as dataset lookups, so a warm request makes no Redis round trip
        cacheManager.Register(
            MagicStrings.DatasetVisibilityCacheSeed,
            CacheProviderFactories.CreateDatasetVisibilityProvider(),
            appSettings.DatasetVisibilityCacheExpiration,
            appSettings.DatasetLookupCacheExpiration ?? DatasetLookupCache.DefaultExpiration);

        _logger.LogDebug("Registered dataset visibility cache provider with expiration: {Expiration}",
            appSettings.DatasetVisibilityCacheExpiration?.ToString() ?? "default");

        // Register build pending tracker cache provider (used by BuildPendingService)
        cacheManager.Register(
            MagicStrings.BuildPendingTrackerCacheSeed,
            _ => Task.FromResult<byte[]>([]),
            TimeSpan.FromHours(24));

        _logger.LogDebug("Registered build pending tracker cache provider with 24-hour expiration");

        _logger.LogInformation("Cache providers registered successfully");
    }
}
