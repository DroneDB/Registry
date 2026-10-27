#nullable enable
using System;
using System.Linq;
using System.Transactions;
using Hangfire;
using Hangfire.Console;
using Hangfire.MySql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Filters;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Jobs;
using Registry.Web.Utilities;

namespace Registry.Web.Hosting;

/// <summary>
/// Background jobs (Hangfire) of every host that creates or runs jobs: one storage configuration, one
/// global retry policy, the recurring job definitions, and the budgeted servers of the job hosts.
/// </summary>
public static class RegistryJobsServiceCollectionExtensions
{
    /// <summary>Attempts of the global retry policy (a job class or method may declare its own).</summary>
    public const int GlobalRetryAttempts = 1;

    /// <summary>
    /// Hangfire's client, storage and filters (<see cref="DataStoreOptions.HangfireProvider"/>), the global
    /// retry policy and the recurring-job startup task. One configuration for both providers: only the
    /// storage differs (both use data compatibility level 1.8).
    /// </summary>
    public static IServiceCollection AddRegistryJobs(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var store = configuration.GetRegistryOptions<DataStoreOptions>();
        var schedule = configuration.GetRegistryOptions<JobScheduleOptions>();
        var hangfireConnection = configuration.GetConnectionString(MagicStrings.HangfireConnectionName);

        services.AddHangfire((sp, hangfire) =>
        {
            hangfire
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSerilogLogProvider();

            UseStorage(hangfire, store.HangfireProvider, schedule, hangfireConnection);

            InstallJobFilters(sp);
            UseGlobalRetryPolicy();
        });

        // The Hangfire filters, built by DI with their real dependencies (InstallJobFilters)
        foreach (var filter in JobFilterTypes)
            services.AddSingleton(filter);


        // Registered eagerly (not in the lazy configuration callback above), so every host that creates
        // or transitions indexed jobs keeps Indexing jobs discoverable by recovery. Idempotent.
        IndexingStateHandler.EnsureRegistered();

        services.AddStartupTask<HangfireJobsInitializer>();
        return services;
    }

    /// <summary>
    /// The Hangfire servers of a job-executing host, sized by the host budget
    /// (<see cref="HostResourceBudget.Processing"/>): one for the <see cref="JobQueues.Builds"/> queue with
    /// the heavy workers, one for the <see cref="JobQueues.Rebuilds"/> queue with a single worker, one for the other
    /// queues with the light workers.
    /// </summary>
    public static IServiceCollection AddJobServers(this IServiceCollection services)
    {
        services.AddHangfireServer((sp, options) => ConfigureBuildsServer(options, JobBudget(sp)));
        services.AddHangfireServer((sp, options) => ConfigureRebuildsServer(options));
        services.AddHangfireServer((sp, options) => ConfigureJobsServer(options, JobBudget(sp)));
        return services;
    }

    /// <summary>The workers the <see cref="JobQueues.Rebuilds"/> queue gets: one build at a time, whatever the host.</summary>
    internal const int RebuildWorkers = 1;

    internal static void ConfigureBuildsServer(BackgroundJobServerOptions options, ProcessingBudget budget)
    {
        options.ShutdownTimeout = ShutdownTimeouts.HangfireServer;
        options.ServerName = $"{Environment.MachineName}:builds";
        options.WorkerCount = budget.HeavyWorkers;
        options.Queues = [JobQueues.Builds];
    }

    // Its own server: the rebuilds never take a worker of the builds queue, and one worker bounds them to one
    // build at a time (its thread cap is ProcessingBudget.BackgroundRebuildThreads)
    internal static void ConfigureRebuildsServer(BackgroundJobServerOptions options)
    {
        options.ShutdownTimeout = ShutdownTimeouts.HangfireServer;
        options.ServerName = $"{Environment.MachineName}:rebuilds";
        options.WorkerCount = RebuildWorkers;
        options.Queues = [JobQueues.Rebuilds];
    }

    internal static void ConfigureJobsServer(BackgroundJobServerOptions options, ProcessingBudget budget)
    {
        options.ShutdownTimeout = ShutdownTimeouts.HangfireServer;
        options.ServerName = $"{Environment.MachineName}:jobs";
        options.WorkerCount = budget.LightWorkers;
        // "tasks" carries the heavy-task substrate jobs that do not build (exports run on
        // "builds"); without it here those jobs would never run.
        options.Queues = JobQueues.Light;
    }

    /// <summary>
    /// The filters every host installs, in order (<see cref="InstallJobFilters"/>): Hangfire runs the state filters in
    /// this order, so <see cref="JobIndexStateFilter"/> has written the job's state before the build filters read the
    /// job index.
    /// </summary>
    internal static readonly Type[] JobFilterTypes =
    [
        typeof(JobIndexStateFilter), typeof(BuildJobFailureFilter), typeof(BuildLogCaptureFilter),
        typeof(BuildRetrySchedulerFilter), typeof(BuildNotificationFilter), typeof(BuildQueueRoutingFilter),
        typeof(JobOperationFilter)
    ];

    /// <summary>
    /// Installs the DI-built instance of each <see cref="JobFilterTypes"/> filter into Hangfire's
    /// global filters, replacing an instance of the same type: building another host (the web host
    /// and a node in one process, tests) never adds a second copy.
    /// </summary>
    public static void InstallJobFilters(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var filters = GlobalJobFilters.Filters;
        lock (filters)
        {
            foreach (var type in JobFilterTypes)
            {
                foreach (var existing in filters.Select(f => f.Instance).Where(i => i.GetType() == type).ToList())
                    filters.Remove(existing);

                filters.Add(services.GetRequiredService(type));
            }
        }
    }

    /// <summary>
    /// Makes one <see cref="AutomaticRetryAttribute"/> with <see cref="GlobalRetryAttempts"/> the global retry
    /// policy: replaces every global one (Hangfire's default included), so building another host never
    /// adds one more.
    /// </summary>
    public static void UseGlobalRetryPolicy()
    {
        var filters = GlobalJobFilters.Filters;
        lock (filters)
        {
            foreach (var existing in filters.Select(f => f.Instance).OfType<AutomaticRetryAttribute>().ToList())
                filters.Remove(existing);

            filters.Add(new AutomaticRetryAttribute { Attempts = GlobalRetryAttempts });
        }
    }

    private static ProcessingBudget JobBudget(IServiceProvider sp) =>
        sp.GetRequiredService<HostResourceBudget>().Processing ??
        throw new InvalidOperationException("Job servers are registered on a host that runs no jobs");

    private static void UseStorage(IGlobalConfiguration hangfire, HangfireProvider provider,
        JobScheduleOptions schedule, string? connectionString)
    {
        var retention = TimeSpan.FromDays(Math.Max(1, schedule.HangfireJobRetentionDays));

        switch (provider)
        {
            case HangfireProvider.InMemory:
                hangfire.UseConsole().UseInMemoryStorage();
                break;

            case HangfireProvider.Mysql:
                hangfire
                    .UseConsole(new ConsoleOptions { FollowJobRetentionPolicy = false, ExpireIn = retention })
                    .UseStorage(new MySqlStorage(connectionString, new MySqlStorageOptions
                    {
                        TransactionIsolationLevel = IsolationLevel.ReadCommitted,
                        QueuePollInterval = TimeSpan.FromSeconds(3),
                        JobExpirationCheckInterval = TimeSpan.FromMinutes(15),
                        CountersAggregateInterval = TimeSpan.FromMinutes(5),
                        InvisibilityTimeout = TimeSpan.FromHours(Math.Max(1, schedule.HangfireInvisibilityTimeoutHours)),
                        PrepareSchemaIfNecessary = true,
                        DashboardJobListLimit = 50000,
                        TransactionTimeout = TimeSpan.FromMinutes(30),
                        TablesPrefix = "hangfire"
                    }))
                    .WithJobExpirationTimeout(retention);
                break;

            default:
                throw new InvalidOperationException($"Unsupported hangfire provider: '{provider}'");
        }
    }
}
