#nullable enable
using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Registry.Adapters.Thumbnail;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.HealthChecks;
using Registry.Web.Identity;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using IMetaManager = Registry.Web.Services.Ports.IMetaManager;

namespace Registry.Web.Hosting;

/// <summary>The feature modules of the web host (RQ-07): each registers one feature on top of <c>AddProcessingNodeServices</c>.</summary>
public static class RegistryFeaturesServiceCollectionExtensions
{
    /// <summary>The web managers and their adapters (datasets, objects, users, organizations, shares, tasks, imports, system).</summary>
    public static IServiceCollection AddRegistryDomain(this IServiceCollection services)
    {
        services.AddScoped<IUtils, WebUtils>();
        services.AddSingleton<IDatasetLookupCache, DatasetLookupCache>();
        services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor, DatasetChangeInterceptor>();
        services.AddScoped<IDatasetSummaryProvider, DatasetSummaryProvider>();

        services.AddScoped<IUsersManager, UsersManager>();
        services.AddScoped<IOrganizationsManager, OrganizationsManager>();
        services.AddScoped<IDatasetsManager, DatasetsManager>();
        services.AddScoped<IStacManager, StacManager>();
        services.AddScoped<Services.Stac.StacCatalog>();
        services.AddScoped<Services.Stac.StacUrls>();
        services.AddScoped<Services.Caching.IDatasetListingInvalidator, Services.Caching.DatasetListingInvalidator>();
        services.AddObjectsSlice();
        services.AddScoped<ICadCrsManager, CadCrsManager>();
        services.AddScoped<IShareManager, ShareManager>();
        services.AddScoped<IPushManager, PushManager>();
        services.AddSingleton<IPushSessionStore, PushSessionStore>();
        services.AddScoped<ISystemManager, SystemManager>();
        services.AddScoped<IRescanManager, RescanManager>();
        services.AddScoped<IMetaManager, MetaManager>();
        services.AddScoped<IBuildStatusService, BuildStatusService>();

        // Admin global tasks dashboard and the per-dataset Processing Platform tasks (TasksController)
        services.AddScoped<IAdminTasksManager, AdminTasksManager>();
        services.AddScoped<ITasksManager, TasksManager>();

        // Web-only submission side of the task substrate (gating, quota, runner)
        services.AddProcessingPlatformSubmission();

        // Import orchestration (the import heavy tools are registered by AddImportSources for every job host)
        services.AddScoped<IImportManager, ImportManager>();
        services.AddScoped<IFileUrlImportManager, FileUrlImportManager>();

        // Admin configuration view (GET /sys/config)
        services.AddScoped<IConfigurationDataBuilder, ConfigurationDataBuilder>();

        services.AddSingleton<IBatchTokenGenerator, BatchTokenGenerator>();
        services.AddSingleton<INameGenerator, NameGenerator>();

        // Downloads: per-user limit and the client address behind the proxy
        services.AddTransient<DownloadLimitMiddleware>();
        services.AddTransient<SecurityHeadersMiddleware>();
        services.AddSingleton<IDownloadLimiter, DownloadLimiter>();
        services.AddSingleton<IClientIpResolver, ClientIpResolver>();

        return services;
    }

    /// <summary>
    /// The objects slice (RQ-11): the entry, transfer, render, build and raster services the object controllers
    /// use, their shared collaborators, and the <see cref="IObjectsManager"/> facade for the other features.
    /// </summary>
    public static IServiceCollection AddObjectsSlice(this IServiceCollection services)
    {
        // libddb's build manifest by role (XI-04): render sources, build downloads, build files, build copies
        services.AddSingleton<IBuildArtifactResolver, BuildArtifactResolver>();

        services.AddScoped<RenderSources>();
        services.AddScoped<EntryJobs>();
        services.AddScoped<EntryChangeNotifier>();
        services.AddScoped<EntryWritePolicy>();

        services.AddScoped<IEntryQueryService, EntryQueryService>();
        services.AddScoped<IEntryCommandService, EntryCommandService>();
        services.AddScoped<IEntryTransferService, EntryTransferService>();
        services.AddScoped<IObjectRenderService, ObjectRenderService>();
        services.AddScoped<IBuildQueryService, BuildQueryService>();
        services.AddScoped<IRasterAnalysisService, RasterAnalysisService>();
        services.AddScoped<IRasterProcessingService, RasterProcessingService>();

        services.AddScoped<IObjectsManager, ObjectsManager>();
        return services;
    }

    /// <summary>OGC services (WMS/WMTS/WFS/WCS/OGC API + MVT endpoint) and their filters.</summary>
    public static IServiceCollection AddRegistryOgc(this IServiceCollection services)
    {
        services.AddScoped<IOgcBuildArtifactPaths, OgcBuildArtifactPaths>();
        services.AddScoped<IOgcLayerCatalog, OgcLayerCatalog>();
        services.AddScoped<IOgcRasterTileSource, OgcRasterTileSource>();
        services.AddScoped<IWmtsManager, WmtsManager>();
        services.AddScoped<IWmsManager, WmsManager>();
        services.AddScoped<IWfsManager, WfsManager>();
        services.AddScoped<IWcsCoverageService, WcsCoverageService>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol10Handler>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol11Handler>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol20Handler>();
        services.AddScoped<IWcsManager, WcsManager>();
        services.AddScoped<IOgcApiFeaturesManager, OgcApiFeaturesManager>();
        services.AddScoped<IOgcApiTilesManager, OgcApiTilesManager>();
        services.AddScoped<OgcAuthorizationFilter>();
        services.AddScoped<OgcExceptionFilter>();
        return services;
    }

    /// <summary>
    /// Native renders share a concurrency budget per lane (REG-07 §5.2, widths from the host budget);
    /// every thumbnail goes through the Heavy lane, whichever generator (local, or the remote thumbnail
    /// generator host when <see cref="ThumbnailOptions.RemoteThumbnailGeneratorUrl"/> is set) makes it.
    /// </summary>
    public static IServiceCollection AddRegistryThumbnails(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<INativeWorkGate, NativeWorkGate>();

        if (!string.IsNullOrWhiteSpace(configuration.GetRegistryOptions<ThumbnailOptions>().RemoteThumbnailGeneratorUrl))
        {
            services.AddOptions<RemoteThumbnailGeneratorSettings>().Configure<IOptions<ThumbnailOptions>>((options, thumbnails) =>
            {
                options.Url = thumbnails.Value.RemoteThumbnailGeneratorUrl!;
                options.Token = thumbnails.Value.ThumbnailGeneratorToken;
            });
            services.AddSingleton<RemoteThumbnailGenerator>();
            services.AddSingleton<IThumbnailGenerator>(sp => new GatedThumbnailGenerator(
                sp.GetRequiredService<RemoteThumbnailGenerator>(), sp.GetRequiredService<INativeWorkGate>()));
        }
        else
        {
            services.AddSingleton<LocalThumbnailGenerator>();
            services.AddSingleton<IThumbnailGenerator>(sp => new GatedThumbnailGenerator(
                sp.GetRequiredService<LocalThumbnailGenerator>(), sp.GetRequiredService<INativeWorkGate>()));
        }

        return services;
    }

    /// <summary>
    /// Upload pipeline (UPL-01): per-request context, quota reservations, receipts, coalesced
    /// invalidation and bounded thumbnail pre-generation.
    /// </summary>
    public static IServiceCollection AddUploadPipeline(this IServiceCollection services)
    {
        services.AddScoped<IUploadManager, UploadManager>();
        services.AddScoped<IChunkedUploadManager, ChunkedUploadManager>();
        services.AddSingleton<IChunkStore, ChunkStore>();
        services.AddSingleton<UploadQuotaLedger>();
        services.AddScoped<IUploadQuotaGuard, UploadQuotaGuard>();
        services.AddSingleton<IUploadReceiptStore, DistributedUploadReceiptStore>();
        services.AddSingleton<DatasetChangeCoalescer>();
        services.AddSingleton<IDatasetChangeCoalescer>(sp => sp.GetRequiredService<DatasetChangeCoalescer>());
        services.AddHostedService(sp => sp.GetRequiredService<DatasetChangeCoalescer>());
        services.AddSingleton<ThumbnailPrecomputeQueue>();
        services.AddSingleton<IThumbnailPrecomputeQueue>(sp => sp.GetRequiredService<ThumbnailPrecomputeQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<ThumbnailPrecomputeQueue>());
        return services;
    }

    /// <summary>The health checks (<c>/health</c>, <c>/quickhealth</c>).</summary>
    public static IServiceCollection AddRegistryHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var storage = configuration.GetRegistryOptions<StorageOptions>();

        var checks = services.AddHealthChecks()
            .AddCheck<CacheHealthCheck>("Cache health check", null, ["service"])
            .AddCheck<DdbHealthCheck>("DroneDB health check", null, ["service"])
            .AddCheck<UserManagerHealthCheck>("User manager health check", null, ["database"])
            .AddDbContextCheck<RegistryContext>("Registry database health check", null, ["database"])
            .AddDbContextCheck<ApplicationDbContext>("Registry identity database health check", null,
                ["database"])
            .AddDiskSpaceHealthCheck(storage.DatasetsPath, "Ddb datasets path space health check", null,
                ["storage"])
            .AddDiskSpaceHealthCheck(storage.CachePath, "Ddb cache path space health check", null,
                ["storage"])
            .AddDiskSpaceHealthCheck(storage.TempPath, "Ddb temp path space health check", null,
                ["storage"])
            .AddDiskSpaceHealthCheck(storage.StoragePath, "Ddb storage path space health check", null,
                ["storage"])
            .AddHangfire(options => { options.MinimumAvailableServers = 1; }, "Hangfire health check", null,
                ["processing"])
            .AddCheck<HangFireHealthCheck>("Hangfire processing health check", null, ["processing"])
            .AddCheck<ThumbnailGeneratorHealthCheck>("Thumbnail generator health check", null, ["service"]);

        if (configuration.GetRegistryOptions<IdentityProviderOptions>().LdapEnabled)
            checks.AddCheck<LdapHealthCheck>("LDAP health check", null, ["service"]);

        return services;
    }

    /// <summary>
    /// The web host's startup work: the thread pool minimum, the database (migrations, initial data,
    /// default admin; the nodes never migrate) and, after the start, the dataset visibility preload.
    /// </summary>
    public static IServiceCollection AddRegistryWebStartup(this IServiceCollection services)
    {
        services.AddStartupTask<ThreadPoolInitializer>();
        services.AddStartupTask<DatabaseInitializer>();
        services.AddBackgroundStartupTask<DatasetVisibilityCacheInitializer>();
        return services;
    }
}
