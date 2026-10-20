using System;
using System.Configuration;
using System.Globalization;
using System.Threading.Tasks;
using System.Transactions;
using Hangfire;
using Hangfire.Console;
using Hangfire.Console.Progress;
using Hangfire.MySql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Filters;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Ports;
using Serilog;
using StackExchange.Redis;

namespace Registry.Web.Utilities;

public static class StartupExtenders
{
    /// <summary>
    /// Configures a DbContext with the specified provider (Sqlite or MySql)
    /// </summary>
    public static void AddDbContextWithProvider<T>(this IServiceCollection services,
        IConfiguration configuration,
        DbProvider provider,
        string connectionStringName,
        string migrationsNamespace) where T : DbContext
    {
        var connectionString = configuration.GetConnectionString(connectionStringName);

        services.AddDbContext<T>((sp, options) =>
        {
            // Hooks on saved changes (e.g. DatasetChangeInterceptor) registered by the host
            options.AddInterceptors(sp.GetServices<Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor>());
            _ = provider switch
            {
                DbProvider.Sqlite => options.UseSqlite(connectionString,
                    x => x.MigrationsAssembly($"Registry.Web.{migrationsNamespace}.SqliteMigrations")),
                DbProvider.Mysql => options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                    x =>
                    {
                        x.EnableRetryOnFailure();
                        x.MigrationsAssembly($"Registry.Web.{migrationsNamespace}.MySqlMigrations");
                    }),
                _ => throw new ArgumentOutOfRangeException(nameof(provider), $"Unrecognised provider: '{provider}'")
            };
        });
    }

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

                    var buildFailureLogger = sp.GetRequiredService<ILogger<BuildJobFailureFilter>>();
                    configuration.UseFilter(new BuildJobFailureFilter(sp, buildFailureLogger));

                    var buildLogLogger = sp.GetRequiredService<ILogger<BuildLogCaptureFilter>>();
                    configuration.UseFilter(new BuildLogCaptureFilter(sp, buildLogLogger));

                    var buildRetryLogger = sp.GetRequiredService<ILogger<BuildRetrySchedulerFilter>>();
                    configuration.UseFilter(new BuildRetrySchedulerFilter(sp, buildRetryLogger));

                    configuration.UseFilter(CreateBuildQueueRoutingFilter(sp));
                });

                break;

            case HangfireProvider.Mysql:

                services.AddHangfire((sp, configuration) =>
                {
                    var logger = sp.GetRequiredService<ILogger<JobIndexStateFilter>>();
                    var buildFailureLogger = sp.GetRequiredService<ILogger<BuildJobFailureFilter>>();

                    configuration
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseSerilogLogProvider()
                        .UseConsole(new ConsoleOptions
                        {
                            FollowJobRetentionPolicy = false,
                            ExpireIn = TimeSpan.FromDays(Math.Max(1, appSettings.HangfireJobRetentionDays))
                        })
                        .UseStorage(new MySqlStorage(
                            appConfig.GetConnectionString(MagicStrings.HangfireConnectionName),
                            new MySqlStorageOptions
                            {
                                TransactionIsolationLevel = IsolationLevel.ReadCommitted,
                                QueuePollInterval = TimeSpan.FromSeconds(3),
                                JobExpirationCheckInterval = TimeSpan.FromMinutes(15),
                                CountersAggregateInterval = TimeSpan.FromMinutes(5),
                                InvisibilityTimeout = TimeSpan.FromHours(Math.Max(1, appSettings.HangfireInvisibilityTimeoutHours)),
                                PrepareSchemaIfNecessary = true,
                                DashboardJobListLimit = 50000,
                                TransactionTimeout = TimeSpan.FromMinutes(30),
                                TablesPrefix = "hangfire"
                            }))
                        .WithJobExpirationTimeout(TimeSpan.FromDays(Math.Max(1, appSettings.HangfireJobRetentionDays)))
                        .UseFilter(new JobIndexStateFilter(sp, logger))
                        .UseFilter(new BuildJobFailureFilter(sp, buildFailureLogger))
                        .UseFilter(new BuildLogCaptureFilter(sp, sp.GetRequiredService<ILogger<BuildLogCaptureFilter>>()))
                        .UseFilter(new BuildRetrySchedulerFilter(sp, sp.GetRequiredService<ILogger<BuildRetrySchedulerFilter>>()))
                        .UseFilter(CreateBuildQueueRoutingFilter(sp));

                });

                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported hangfire provider: '{appSettings.HangfireProvider}'");
        }

        // Specify the global number of retries
        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 1 });

        // Registered eagerly (not in the lazy configuration callback above) for both providers, so
        // every host that creates or transitions indexed jobs keeps Indexing jobs discoverable by
        // recovery. Idempotent: hosts and tests may call this more than once.
        IndexingStateHandler.EnsureRegistered();
    }

    private static BuildQueueRoutingFilter CreateBuildQueueRoutingFilter(IServiceProvider sp) =>
        new((toolId, version) =>
            sp.GetService<IHeavyToolRegistry>()?.Resolve(toolId, version)?.RunsNativeBuild == true);

    /// <summary>
    /// Registers the Hangfire servers of a job-executing host (REG-09): one for the
    /// <see cref="JobQueues.Builds"/> queue with <see cref="ProcessingBudget.HeavyWorkers"/> workers,
    /// one for the other queues with <see cref="ProcessingBudget.LightWorkers"/> workers, and passes
    /// the per-build limits to libddb.
    /// </summary>
    public static void AddBudgetedHangfireServers(this IServiceCollection services, ProcessingBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        ApplyNativeBuildBudget(budget);

        services.AddHangfireServer(options =>
        {
            options.ServerName = $"{Environment.MachineName}:builds";
            options.WorkerCount = budget.HeavyWorkers;
            options.Queues = [JobQueues.Builds];
        });

        services.AddHangfireServer(options =>
        {
            options.ServerName = $"{Environment.MachineName}:jobs";
            options.WorkerCount = budget.LightWorkers;
            // "tasks" carries the heavy-task substrate jobs that do not build (exports run on
            // "builds"); without it here those jobs would never run.
            options.Queues = JobQueues.Light;
        });
    }

    /// <summary>
    /// libddb reads its build limits (DDB-08) from the environment; values set explicitly in the
    /// deployment environment win over the budget.
    /// </summary>
    private static void ApplyNativeBuildBudget(ProcessingBudget budget)
    {
        foreach (var (name, value) in new[]
                 {
                     ("DDB_BUILD_THREADS", budget.BuildThreads),
                     ("DDB_COG_CACHE_MB", budget.BuildCacheMb),
                     ("DDB_WARP_MEMORY_MB", budget.BuildWarpMemoryMb)
                 })
        {
            if (!NativeEnvironment.SetIfUnset(name, value.ToString(CultureInfo.InvariantCulture)))
                Log.Information("Keeping {Name}={Value} from the environment", name, NativeEnvironment.Get(name));
        }
    }

    public static void AddCacheProvider(this IServiceCollection services, AppSettings appSettings)
    {
        // OGC document keys carry a per-dataset generation kept in the distributed cache
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