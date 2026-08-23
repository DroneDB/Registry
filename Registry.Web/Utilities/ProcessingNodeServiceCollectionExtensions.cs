using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities;

/// <summary>
/// DI registration for the application services shared by every host that executes
/// background jobs and heavy tools (the processing node, and any other Hangfire worker host).
/// </summary>
/// <remarks>
/// Extracted from <c>Program.RunAsProcessingNode</c> so the exact same container can be built
/// in tests (see <c>ProcessingNodeDiCompletenessTests</c>) to assert that every service the
/// heavy tools resolve at runtime is registered - catching the class of bug where a tool
/// resolves a service that is only registered on the full web host.
/// </remarks>
public static class ProcessingNodeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application services required to execute background jobs and heavy tools.
    /// Does not register the Hangfire server or hosted services, which are runtime concerns
    /// owned by the host.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="appSettings">The strongly-typed application settings.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddProcessingNodeServices(
        this IServiceCollection services, IConfiguration configuration, AppSettings appSettings)
    {
        // Strongly-typed settings (IOptions<AppSettings>) consumed by several tools/services.
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));

        // Register database context required for job processing
        services.AddDbContextWithProvider<RegistryContext>(configuration, appSettings.RegistryProvider,
            MagicStrings.RegistryConnectionName, "Data");

        // Register cache services (required by BuildPendingService)
        services.AddMemoryCache();
        services.AddCacheProvider(appSettings);

        // Register job indexing services (required by JobIndexSyncService and BackgroundJobsProcessor)
        services.AddJobIndexing();

        // Register HTTP client factory for services that need to make HTTP calls
        services.AddHttpClient();

        // Register core singleton services
        services.AddSingleton<ICacheManager, CacheManager>();
        services.AddSingleton<IDdbWrapper, NativeDdbWrapper>();
        services.AddSingleton<IFileSystem, FileSystem>();

        // Per-dataset index write coalescer (process-local singleton): coalesces concurrent index
        // writes on THIS host - notably the reconciliation sweep - into one native batch.
        // SQLite serializes across processes regardless, so this does not (and cannot) keep a
        // cross-process lane shared with the web host; it only keeps this host's write bursts
        // aligned on a single lane.
        services.AddSingleton<IDatasetIndexQueue, DatasetIndexQueue>();

        // Register scoped services required by background jobs
        services.AddScoped<IDdbManager, DdbManager>();
        services.AddScoped<IBackgroundJobsProcessor, BackgroundJobsProcessor>();
        services.AddScoped<BuildPendingService>();
        services.AddScoped<JobIndexSyncService>();
        services.AddScoped<DatasetCleanupService>();
        services.AddScoped<OrphanedDatasetCleanupService>();
        services.AddScoped<RecurringDatasetCleanupService>();
        services.AddScoped<ArtifactCompletenessCheckerService>();
        services.AddScoped<IndexReconciliationService>();

        // Processing Platform task substrate (native tools incl. build/raster-export)
        services.AddProcessingPlatform();

        // Import Dataset feature (the import-dataset heavy tool runs on the worker host).
        services.AddImportSources(appSettings);

        return services;
    }
}
