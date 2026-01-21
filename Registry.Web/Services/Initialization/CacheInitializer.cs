using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Handles cache initialization including connection validation and cache provider registration.
/// </summary>
internal class CacheInitializer
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public CacheInitializer(IServiceProvider services, ILogger logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        _logger.LogInformation("Cache initialization starting");

        var appSettings = _services.GetRequiredService<IOptions<AppSettings>>().Value;

        await ValidateCacheConnectionAsync(appSettings, token);
        RegisterCacheProviders(appSettings);

        _logger.LogInformation("Cache initialization completed");
    }

    private async Task ValidateCacheConnectionAsync(AppSettings appSettings, CancellationToken token)
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

    private void RegisterCacheProviders(AppSettings appSettings)
    {
        _logger.LogInformation("Registering cache providers");

        var cacheManager = _services.GetRequiredService<ICacheManager>();

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

        // Register dataset visibility cache provider
        cacheManager.Register(
            MagicStrings.DatasetVisibilityCacheSeed,
            CacheProviderFactories.CreateDatasetVisibilityProvider(),
            appSettings.DatasetVisibilityCacheExpiration);

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
