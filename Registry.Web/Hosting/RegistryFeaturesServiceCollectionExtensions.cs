#nullable enable
using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.HealthChecks;
using Registry.Web.Identity;
using Registry.Web.Middlewares;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Configuration;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Features;
using Registry.Web.Services.Import;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Maintenance;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Services.Managers.Wfs;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Organizations;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Processing;
using Registry.Web.Services.Push;
using Registry.Web.Services.RemoteImport;
using Registry.Web.Services.Shares;
using Registry.Web.Services.Uploads;
using Registry.Web.Services.Users;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using IMetaManager = Registry.Web.Services.Ports.IMetaManager;

namespace Registry.Web.Hosting;

/// <summary>The feature modules of the web host: each registers one feature on top of <c>AddProcessingNodeServices</c>.</summary>
public static class RegistryFeaturesServiceCollectionExtensions
{
    /// <summary>The web managers and their adapters (datasets, objects, users, organizations, shares, tasks, imports, system).</summary>
    public static IServiceCollection AddRegistryDomain(this IServiceCollection services)
    {
        services.AddScoped<IDatasetResolver, DatasetResolver>();
        services.AddScoped<IUrlBuilder, UrlBuilder>();
        services.AddScoped<IStorageQuota, StorageQuota>();
        services.AddSingleton<IDatasetLookupCache, DatasetLookupCache>();
        services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.ISaveChangesInterceptor, DatasetChangeInterceptor>();
        services.AddScoped<IDatasetSummaryProvider, DatasetSummaryProvider>();

        services.AddScoped<UsersManager>();
        services.AddScoped<JwtTokenIssuer>();
        services.AddScoped<UserProvisioning>();
        services.AddScoped<IUserSessions, UserSessions>();
        services.AddScoped<UserAccounts>();
        services.AddScoped<UserPreferences>();
        services.AddScoped<NotificationStatus>();
        services.AddScoped<UserPasswords>();
        services.AddScoped<UserRoles>();
        services.AddScoped<UserDirectory>();
        services.AddScoped<UserDeletion>();
        services.AddScoped<IOrganizationsManager, OrganizationsManager>();
        services.AddScoped<OrganizationQueries>();
        services.AddScoped<OrganizationCommands>();
        services.AddScoped<OrganizationMerger>();
        services.AddScoped<OrganizationMembers>();
        services.AddScoped<IDatasetsManager, DatasetsManager>();
        services.AddScoped<DatasetQueries>();
        services.AddScoped<DatasetCommands>();
        services.AddScoped<DatasetMover>();
        services.AddScoped<StacManager>();
        services.AddScoped<Services.Stac.StacCatalog>();
        services.AddScoped<Services.Stac.StacUrls>();
        services.AddScoped<Services.Caching.IDatasetListingInvalidator, Services.Caching.DatasetListingInvalidator>();
        services.AddObjectsSlice();
        services.AddScoped<CadCrsManager>();
        services.AddScoped<ShareManager>();
        services.AddScoped<ShareBatchStore>();
        services.AddScoped<ShareBatchRollback>();
        services.AddScoped<ShareTargets>();
        services.AddScoped<ShareBatches>();
        services.AddScoped<ShareUploads>();
        services.AddScoped<ShareChunkedUploads>();
        services.AddScoped<PushManager>();
        services.AddScoped<PushSessions>();
        services.AddScoped<PushUploads>();
        services.AddScoped<PushCommit>();
        services.AddSingleton<PushSessionStore>();
        services.AddSystemAdministration();
        services.AddScoped<RescanManager>();
        services.AddScoped<IMetaManager, MetaManager>();
        services.AddScoped<IBuildStatusService, BuildStatusService>();

        // Admin global tasks dashboard and the per-dataset Processing Platform tasks (TasksController)
        services.AddScoped<AdminTasksManager>();
        services.AddScoped<ITasksManager, TasksManager>();
        services.AddScoped<TaskSubmission>();
        services.AddScoped<TaskHistory>();
        services.AddScoped<TaskLifecycle>();
        services.AddScoped<TaskRetry>();
        services.AddScoped<TaskAuthorizer>();
        services.AddScoped<TaskArtifactStore>();

        // Web-only submission side of the task substrate (gating, quota, runner)
        services.AddProcessingPlatformSubmission();

        // Import orchestration (the import heavy tools are registered by AddImportSources for every job host)
        services.AddScoped<ImportManager>();
        services.AddScoped<ImportQuotaGate>();
        services.AddScoped<ImportTargetDatasets>();
        services.AddScoped<FileUrlImportManager>();

        services.AddSingleton<IBatchTokenGenerator, BatchTokenGenerator>();
        services.AddSingleton<INameGenerator, NameGenerator>();

        // Downloads: per-user limit and the client address behind the proxy
        services.AddTransient<DownloadLimitMiddleware>();
        services.AddTransient<SecurityHeadersMiddleware>();
        services.AddSingleton<IDownloadLimiter, DownloadLimiter>();
        services.AddSingleton<ClientIpResolver>();

        services.AddUpdateCheckWeb();

        return services;
    }

    /// <summary>
    /// The <c>/sys</c> features: data and build maintenance, the slug repair, the global report, the
    /// configuration view, the remote import (queued; the job is registered by <c>AddImportSources</c>), the
    /// processing nodes and the platform features.
    /// </summary>
    public static IServiceCollection AddSystemAdministration(this IServiceCollection services)
    {
        services.AddScoped<SystemManager>();
        services.AddScoped<DatasetSlugRepair>();
        services.AddScoped<GlobalReportBuilder>();
        services.AddScoped<BuildMaintenance>();
        services.AddSingleton<ConfigurationView>();
        services.AddScoped<ConfigurationDataBuilder>();
        services.AddScoped<IConfigurationSectionBuilder, IdentitySections>();
        services.AddScoped<IConfigurationSectionBuilder, EmailSections>();
        services.AddScoped<IConfigurationSectionBuilder, DataStoreSections>();
        services.AddScoped<IConfigurationSectionBuilder, TransferSections>();
        services.AddScoped<IConfigurationSectionBuilder, JobScheduleSections>();
        services.AddScoped<IConfigurationSectionBuilder, HubSections>();
        services.AddScoped<IConfigurationSectionBuilder, ProcessingSections>();
        services.AddScoped<IConfigurationSectionBuilder, ImportSections>();
        services.AddScoped<IConfigurationSectionBuilder, HostSections>();
        services.AddScoped<IConfigurationSectionBuilder, UpdateCheckSections>();

        services.AddSingleton<HangfireJobOutcomeReader>();
        services.AddScoped<RemoteImportService>();

        services.AddScoped<ProcessingNodeCatalog>();

        services.AddScoped<FeaturesBuilder>();
        services.AddScoped<IFeatureSection, AccountFeatures>();
        services.AddScoped<IFeatureSection, EmailFeatures>();
        services.AddScoped<IFeatureSection, DownloadFeatures>();
        services.AddScoped<IFeatureSection, TaskFeatures>();
        services.AddScoped<IFeatureSection, HubFeatures>();
        services.AddScoped<IFeatureSection, EntryTypeFeatures>();
        services.AddScoped<IFeatureSection, ArchiveFeatures>();
        services.AddScoped<IFeatureSection, UpdateCheckFeatures>();
        return services;
    }

    /// <summary>
    /// The objects slice: the entry, transfer, render, build and raster services the object controllers
    /// use, their shared collaborators, and the <see cref="IObjectsManager"/> facade for the other features.
    /// </summary>
    public static IServiceCollection AddObjectsSlice(this IServiceCollection services)
    {
        services.AddScoped<RenderSources>();
        services.AddScoped<EntryJobs>();
        services.AddScoped<EntryChangeNotifier>();
        services.AddScoped<EntryWritePolicy>();

        services.AddScoped<IEntryQueryService, EntryQueryService>();
        services.AddScoped<IEntryCommandService, EntryCommandService>();
        services.AddScoped<IEntryTransferService, EntryTransferService>();
        services.AddScoped<IObjectRenderService, ObjectRenderService>();
        services.AddScoped<BuildQueryService>();
        services.AddScoped<RasterAnalysisService>();
        services.AddScoped<RasterProcessingService>();

        services.AddScoped<IObjectsManager, ObjectsManager>();
        return services;
    }

    /// <summary>OGC services (WMS/WMTS/WFS/WCS/OGC API + MVT endpoint) and their filters.</summary>
    public static IServiceCollection AddRegistryOgc(this IServiceCollection services)
    {
        services.AddScoped<OgcManagerDependencies>();
        services.AddScoped<IOgcLayerCatalog, OgcLayerCatalog>();
        services.AddScoped<IOgcRasterTileSource, OgcRasterTileSource>();
        services.AddScoped<RasterInfoCache>();
        services.AddScoped<IRasterIndexCatalog, RasterIndexCatalog>();
        services.AddScoped<WmtsManager>();
        services.AddScoped<WmsManager>();
        services.AddScoped<WfsManager>();
        services.AddScoped<WfsCapabilities>();
        services.AddScoped<WfsSchema>();
        services.AddScoped<WfsFeatureQuery>();
        services.AddScoped<WcsCoverageService>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol10Handler>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol11Handler>();
        services.AddScoped<IWcsProtocolHandler, WcsProtocol20Handler>();
        services.AddScoped<WcsManager>();
        services.AddScoped<OgcApiFeaturesManager>();
        services.AddScoped<OgcApiTilesManager>();
        services.AddScoped<OgcAuthorizationFilter>();
        services.AddScoped<OgcExceptionFilter>();
        return services;
    }

    /// <summary>
    /// Native renders share a concurrency budget per lane (widths from the host budget);
    /// every thumbnail goes through the Heavy lane, whichever generator (local, or the remote thumbnail
    /// generator host when <see cref="ThumbnailOptions.RemoteThumbnailGeneratorUrl"/> is set) makes it.
    /// </summary>
    public static IServiceCollection AddRegistryThumbnails(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<INativeWorkGate, NativeWorkGate>();

        services.AddThumbnailGeneratorAdapter(configuration);

        return services;
    }

    /// <summary>
    /// Upload pipeline: per-request context, quota reservations, receipts, coalesced
    /// invalidation and bounded thumbnail pre-generation.
    /// </summary>
    public static IServiceCollection AddUploadPipeline(this IServiceCollection services)
    {
        services.AddScoped<UploadManager>();
        services.AddScoped<UploadFiles>();
        services.AddScoped<UploadReceiptTracker>();
        services.AddScoped<UploadIndexer>();
        services.AddScoped<UploadFinalizer>();
        services.AddScoped<UploadCommitter>();
        services.AddScoped<ChunkedUploadManager>();
        services.AddSingleton<ChunkStore>();
        services.AddSingleton<UploadQuotaLedger>();
        services.AddScoped<UploadQuotaGuard>();
        services.AddSingleton<DistributedUploadReceiptStore>();
        services.AddSingleton<DatasetChangeCoalescer>();
        services.AddSingleton<IDatasetChangeCoalescer>(sp => sp.GetRequiredService<DatasetChangeCoalescer>());
        services.AddHostedService(sp => sp.GetRequiredService<DatasetChangeCoalescer>());
        services.AddSingleton<ThumbnailPrecomputeQueue>();
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

        // A singleton (registered by AddEmail): it caches its result
        if (configuration.GetRegistryOptions<SmtpSettings>().Enabled)
            checks.AddCheck<SmtpHealthCheck>("SMTP health check", null, ["service"]);

        return services;
    }

    /// <summary>
    /// The web host's startup work: the thread pool minimum, the database (migrations, initial data,
    /// default admin; the nodes never migrate) and, after the start, the dataset visibility preload.
    /// </summary>
    public static IServiceCollection AddRegistryWebStartup(this IServiceCollection services)
    {
        services.AddStartupTask<ThreadPoolInitializer>();
        services.AddScoped<DatabaseMigrator>();
        services.AddScoped<DefaultAdminProvisioner>();
        services.AddStartupTask<DatabaseInitializer>();
        services.AddBackgroundStartupTask<DatasetVisibilityCacheInitializer>();
        return services;
    }
}
