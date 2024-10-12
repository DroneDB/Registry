using System;
using System.Configuration;
using System.Transactions;
using Hangfire;
using Hangfire.Console;
using Hangfire.Console.Progress;
using Hangfire.MySql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Models.Configuration;


namespace Registry.Web.Utilities;

public static class StartupExtenders
{
    public static void AddHangfireProvider(this IServiceCollection services, AppSettings appSettings,
        IConfiguration appConfig)
    {
        switch (appSettings.HangfireProvider)
        {
            case HangfireProvider.InMemory:

                services.AddHangfire(configuration => configuration
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseSerilogLogProvider()
                    .UseConsole()
                    .UseInMemoryStorage());

                break;

            case HangfireProvider.Mysql:

                // Specify only one worker

                services.AddHangfire(configuration => configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
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
                                JobExpirationCheckInterval = TimeSpan.FromHours(1),
                                CountersAggregateInterval = TimeSpan.FromMinutes(5),
                                PrepareSchemaIfNecessary = true,
                                DashboardJobListLimit = 50000,
                                TransactionTimeout = TimeSpan.FromMinutes(10),
                                TablesPrefix = "hangfire"
                            }))
                        .WithJobExpirationTimeout(TimeSpan.FromDays(30)));

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
}