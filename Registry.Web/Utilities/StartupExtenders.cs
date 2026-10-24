using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using StackExchange.Redis;
using Registry.Web.Services.Caching;

namespace Registry.Web.Utilities;

public static class StartupExtenders
{
    /// <summary>
    /// Configures a DbContext with the specified provider (Sqlite or MySql)
    /// </summary>
    /// <param name="mySqlVersions">MySQL server version source; defaults to one built from
    /// <paramref name="configuration"/>, which probes once per connection string per registration
    /// (not once per scope) unless the version is pinned.</param>
    public static void AddDbContextWithProvider<T>(this IServiceCollection services,
        IConfiguration configuration,
        DbProvider provider,
        string connectionStringName,
        string migrationsNamespace,
        MySqlServerVersionCache mySqlVersions = null) where T : DbContext
    {
        var connectionString = configuration.GetConnectionString(connectionStringName);
        mySqlVersions ??= MySqlServerVersionCache.FromConfiguration(configuration);

        var migrationsAssembly = DbProviderConfigurator.MigrationsAssemblyName(migrationsNamespace, provider);

        services.AddDbContext<T>((sp, options) =>
        {
            // Hooks on saved changes (e.g. DatasetChangeInterceptor) registered by the host
            options.AddInterceptors(sp.GetServices<Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor>());
            DbProviderConfigurator.Configure(options, provider, connectionString, migrationsAssembly, mySqlVersions.Resolve);
        });
    }

    public static void AddCacheProvider(this IServiceCollection services, CachingOptions appSettings)
    {
        // Records computed on a miss (single-flight, fail-open) and the cache generations their keys carry
        // (OGC documents: one per dataset, kept in the distributed cache)
        services.AddRecordCache();
        services.AddSingleton<Services.Ports.IOgcCacheKeys, OgcCacheKeys>();

        if (appSettings.CacheProvider == null)
        {
            // Use memory caching
            services.AddDistributedMemoryCache();
            services.AddSingleton<ICacheKeyScanner, NullCacheKeyScanner>();
            services.AddSingleton<Services.Ports.ITaskChangeNotifier, InMemoryTaskChangeNotifier>();
            return;
        }

        switch (appSettings.CacheProvider.Type)
        {
            case CacheType.InMemory:

                services.AddDistributedMemoryCache();
                services.AddSingleton<ICacheKeyScanner, NullCacheKeyScanner>();
                services.AddSingleton<Services.Ports.ITaskChangeNotifier, InMemoryTaskChangeNotifier>();

                break;

            case CacheType.Redis:

                var settings = appSettings.CacheProvider.Settings.ToObject<RedisProviderSettings>();

                if (settings == null)
                    throw new ArgumentException("Invalid redis cache provider settings");

                // Register a shared IConnectionMultiplexer for both caching and pattern-based key scanning
                services.AddSingleton<IConnectionMultiplexer>(sp =>
                    ConnectionMultiplexer.Connect(settings.InstanceAddress));

                services.AddStackExchangeRedisCache(options =>
                {
                    options.InstanceName = settings.InstanceName;
                });

                // Wire the shared multiplexer into Redis cache options to avoid a second connection
                services.AddOptions<Microsoft.Extensions.Caching.StackExchangeRedis.RedisCacheOptions>()
                    .Configure<IConnectionMultiplexer>((options, multiplexer) =>
                    {
                        options.ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer);
                    });

                services.AddSingleton<ICacheKeyScanner>(sp =>
                    new RedisCacheKeyScanner(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        settings.InstanceName,
                        sp.GetRequiredService<ILogger<RedisCacheKeyScanner>>()));

                // Task changes made by processing nodes reach the web node's event streams
                services.AddSingleton<Services.Ports.ITaskChangeNotifier>(sp =>
                    new RedisTaskChangeNotifier(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        settings.InstanceName ?? string.Empty,
                        sp.GetRequiredService<ILogger<RedisTaskChangeNotifier>>()));

                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported caching provider: '{(int)appSettings.CacheProvider.Type}'");
        }
    }

    public static async Task ValidateCacheConnection(CachingOptions appSettings)
    {
        switch (appSettings.CacheProvider?.Type)
        {
            case CacheType.Redis:
            {
                var settings = appSettings.CacheProvider.Settings.ToObject<RedisProviderSettings>();

                if (settings == null)
                    throw new InvalidOperationException("Invalid redis cache provider settings");

                try
                {
                    await using var connection = await ConnectionMultiplexer.ConnectAsync(settings.InstanceAddress);
                    var database = connection.GetDatabase();

                    // Test Redis connection with a simple ping
                    var testKey = $"startup-test-{Guid.NewGuid()}";
                    await database.StringSetAsync(testKey, "test", TimeSpan.FromSeconds(10));
                    var result = await database.StringGetAsync(testKey);
                    await database.KeyDeleteAsync(testKey);

                    if (!result.HasValue || result != "test")
                        throw new InvalidOperationException("Redis connection test failed: unable to read/write data");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Redis connection failed: {ex.Message}. Please ensure Redis is running and accessible at {settings.InstanceAddress}", ex);
                }

                break;
            }
            case CacheType.InMemory:
            case null:
                // No validation needed for in-memory cache
                break;
            default:
                throw new ArgumentOutOfRangeException($"Unsupported caching provider: '{(int)appSettings.CacheProvider.Type}'");
        }
    }
}