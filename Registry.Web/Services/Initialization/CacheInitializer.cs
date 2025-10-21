using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;
using Serilog;
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
        cacheManager.Register(MagicStrings.TileCacheSeed, async parameters =>
        {
            // Parameters: fileHash, tx, ty, tz, retina, generateFunc
            var generateFunc = (Func<Task<byte[]>>)parameters[5];
            var data = await generateFunc();
            return data.ToWebp(90);
        }, appSettings.TilesCacheExpiration);

        _logger.LogDebug("Registered tile cache provider with expiration: {Expiration}",
            appSettings.TilesCacheExpiration?.ToString() ?? "default");

        // Register thumbnail cache provider
        cacheManager.Register(MagicStrings.ThumbnailCacheSeed, async parameters =>
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
        }, appSettings.ThumbnailsCacheExpiration);

        _logger.LogDebug("Registered thumbnail cache provider with expiration: {Expiration}",
            appSettings.ThumbnailsCacheExpiration?.ToString() ?? "default");

        _logger.LogInformation("Cache providers registered successfully");
    }
}
