using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Hosting;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// DI registration for the application services shared by every host that executes background jobs
/// and heavy tools: the processing node, and the web host, which builds on it and adds only its own
/// services. The same container is built in tests (<c>ProcessingNodeDiCompletenessTests</c>,
/// <c>DiValidationTests</c>) to catch a registration a tool needs at runtime.
/// </summary>
public static class ProcessingNodeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options and the application services required to execute background jobs and
    /// heavy tools, and the cache startup task. Does not register Hangfire (<c>AddRegistryJobs</c>,
    /// <c>AddJobServers</c>) or the libddb runtime (<c>RegistryHost</c>): host concerns.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="configuration">The application configuration (instance type included).</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddProcessingNodeServices(this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRegistryOptions(configuration, configuration.GetInstanceType());

        // Registry database, and the identity database, once for every host that runs jobs: the web host's authentication
        // (AddRegistryAuth) and the update e-mail to the administrators, which the job sends from whichever node serves
        // the default queue, read the same one (the connection string of the settings file every service of the
        // deployment shares, or the SQLite file of the shared storage folder)
        var store = configuration.GetRegistryOptions<DataStoreOptions>();
        services.AddDbContextWithProvider<RegistryContext>(configuration, store.RegistryProvider,
            MagicStrings.RegistryConnectionName, "Data");
        services.AddDbContextWithProvider<ApplicationDbContext>(configuration, store.AuthProvider,
            MagicStrings.IdentityConnectionName, "Identity");

        // Caches (required by BuildPendingService and the managers)
        services.AddMemoryCache();
        services.AddCacheProvider(configuration.GetRegistryOptions<CachingOptions>());
        services.AddSingleton<ICacheManager, CacheManager>();
        services.AddStartupTask<CacheInitializer>();

        // Job indexing (required by JobIndexSyncService and BackgroundJobsProcessor)
        services.AddJobIndexing();

        // HTTP client factory for services that need to make HTTP calls
        services.AddHttpClient();

        // Native library and file system
        services.AddNativeDdbWrapper();
        services.AddFileSystemAdapter();
        services.AddDatasetFileSystem();

        // Per-dataset index write coalescer (process-local singleton): coalesces concurrent index
        // writes on THIS host - notably the reconciliation sweep - into one native batch.
        // SQLite serializes across processes regardless, so this does not (and cannot) keep a
        // cross-process lane shared with another host; it only keeps this host's write bursts
        // aligned on a single lane.
        services.AddSingleton<IDatasetIndexQueue, DatasetIndexQueue>();

        // Datasets and the services the background jobs run
        services.AddScoped<IDdbManager, DdbManager>();
        services.AddSingleton<InitializedDatasetFolders>();
        services.AddScoped<IBackgroundJobsProcessor, BackgroundJobsProcessor>();
        services.AddScoped<BuildPendingService>();

        services.AddBuildScheduling();
        services.AddScoped<JobIndexSyncService>();
        services.AddScoped<IndexedJobPublicationRecoveryService>();
        services.AddScoped<DatasetCleanupService>();
        services.AddScoped<OrphanedDatasetCleanupService>();
        services.AddScoped<RecurringDatasetCleanupService>();
        services.AddScoped<ArtifactCompletenessCheckerService>();
        services.AddScoped<IndexRescanService>();
        services.AddScoped<IndexReconciliationService>();

        // The job classes (Hangfire's DI activator creates them; registered so a missing dependency
        // fails the container validation, not the first run) and what they share
        services.AddScoped<DatasetSweep>();
        services.AddScoped<PendingBuildEnqueuer>();
        services.AddScoped<DdbJobs>();
        services.AddScoped<FileJobs>();
        services.AddScoped<HealthCheckJob>();
        services.AddScoped<ExpiredRecurringJobsSweep>();
        services.AddScoped<HangfireUtils>(); // the jobs stored by earlier releases

        // Processing Platform task substrate (native tools incl. build/raster-export)
        services.AddProcessingPlatform();

        // Import Dataset feature (the import-dataset heavy tool runs on the worker host).
        services.AddImportSources(configuration);

        // E-mail: a request queues it, the host that runs the job sends it
        services.AddEmail(configuration);

        // The build notification e-mails: reported where a build ends, sent by whichever host runs the digest
        services.AddBuildNotifications();

        // The identity of the deployment and the update check (the job runs on whichever node processes the default queue)
        services.AddUpdateCheck();

        return services;
    }
}
