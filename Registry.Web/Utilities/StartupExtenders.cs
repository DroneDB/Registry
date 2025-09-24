﻿using System;
using System.Configuration;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire;
using Hangfire.Console;
using Hangfire.Console.Progress;
using Hangfire.MySql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using StackExchange.Redis;

namespace Registry.Web.Utilities;

public static class StartupExtenders
{
    public static void AddHangfireProvider(this IServiceCollection services, AppSettings appSettings,
        IConfiguration appConfig)
    {
        
        switch (appSettings.HangfireProvider)
        {
            case HangfireProvider.InMemory:

                services.AddHangfire((sp, configuration) =>
                {
                    configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseSerilogLogProvider()
                        .UseConsole()
                        .UseInMemoryStorage();

                    var logger = sp.GetRequiredService<ILogger<JobIndexStateFilter>>();
                    configuration.UseFilter(new JobIndexStateFilter(sp, logger));
                });

                break;

            case HangfireProvider.Mysql:

                services.AddHangfire((sp, configuration) =>
                {
                    var logger = sp.GetRequiredService<ILogger<JobIndexStateFilter>>();
                    
                    configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseSerilogLogProvider()
                        .UseConsole(new ConsoleOptions
                        {
                            FollowJobRetentionPolicy = true
                        })
                        .UseStorage(new MySqlStorage(
                            appConfig.GetConnectionString(MagicStrings.HangfireConnectionName),
                            new MySqlStorageOptions
                            {
                                TransactionIsolationLevel = IsolationLevel.ReadCommitted,
                                QueuePollInterval = TimeSpan.FromSeconds(3),
                                JobExpirationCheckInterval = TimeSpan.FromMinutes(15),
                                CountersAggregateInterval = TimeSpan.FromMinutes(5),
                                PrepareSchemaIfNecessary = true,
                                DashboardJobListLimit = 50000,
                                TransactionTimeout = TimeSpan.FromMinutes(30),
                                TablesPrefix = "hangfire"
                            }))
                        .WithJobExpirationTimeout(TimeSpan.FromDays(30))
                        .UseFilter(new JobIndexStateFilter(sp, logger));
                    
                });

                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported hangfire provider: '{appSettings.HangfireProvider}'");
        }

        // Specify the global number of retries
        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 1 });
    }

    public static void AddCacheProvider(this IServiceCollection services, AppSettings appSettings)
    {
        if (appSettings.CacheProvider == null)
        {
            // Use memory caching
            services.AddDistributedMemoryCache();
            return;
        }

        switch (appSettings.CacheProvider.Type)
        {
            case CacheType.InMemory:

                services.AddDistributedMemoryCache();

                break;

            case CacheType.Redis:

                var settings = appSettings.CacheProvider.Settings.ToObject<RedisProviderSettings>();

                if (settings == null)
                    throw new ArgumentException("Invalid redis cache provider settings");

                services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = settings.InstanceAddress;
                    options.InstanceName = settings.InstanceName;
                });

                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported caching provider: '{(int)appSettings.CacheProvider.Type}'");
        }
    }

    public static async Task ValidateCacheConnection(AppSettings appSettings)
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