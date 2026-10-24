#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Registry.Adapters;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Hosting;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Adapters;
using Registry.Web.Services.Builds;

namespace Registry.Web.Test.Support;

/// <summary>
/// The services of the objects slice (RQ-11) and the <see cref="ObjectsManager"/> facade for tests, wired by the
/// production registration (<see cref="RegistryFeaturesServiceCollectionExtensions.AddObjectsSlice"/>) over the
/// collaborators a test names. Every other collaborator gets a default:
/// <list type="bullet">
/// <item>settings: <see cref="TestSettings.Options(Action{AppSettings}?)"/> (the focused options bound from it);
/// loggers: null loggers;</item>
/// <item>file system: the real <see cref="FileSystem"/>; background jobs: <see cref="SimpleBackgroundJobsProcessor"/>;</item>
/// <item>DDB manager, utils, auth, cache, thumbnails, job index query: loose Moq mocks; DDB wrapper: a loose mock
/// for the renders, while the build artifact resolver reads libddb's entry types from the contract fixtures;</item>
/// <item>build-pending service: a real <see cref="BuildPendingService"/> on the SUT context and the DDB manager,
/// background jobs and cache above;</item>
/// <item>upload pipeline: a real <see cref="UploadManager"/> over the same collaborators (indexing with AddRaw
/// unless an index queue is given); OGC catalog and build status: no-op mocks; native gate: the process-wide one
/// with the default widths.</item>
/// </list>
/// </summary>
public sealed class ObjectServicesBuilder
{
    private readonly RegistryContext _context;

    private ILogger<ObjectsManager> _logger = NullLogger<ObjectsManager>.Instance;
    private IOptions<AppSettings>? _settings;
    private IDdbManager? _ddbManager;
    private IDatasetResolver? _datasets;
    private IStorageQuota? _quota;
    private IAuthManager? _authManager;
    private ICacheManager? _cacheManager;
    private IFileSystem? _fileSystem;
    private IBackgroundJobsProcessor? _backgroundJobs;
    private IDdbWrapper? _ddbWrapper;
    private IThumbnailGenerator? _thumbnailGenerator;
    private IJobIndexQuery? _jobIndexQuery;
    private IJobIndexWriter? _jobIndexWriter;
    private BuildPendingService? _buildPendingService;
    private IBuildArtifactResolver? _buildArtifactResolver;
    private IDatasetIndexQueue? _indexQueue;
    private INativeWorkGate? _nativeGate;

    /// <param name="context">The registry context of the SUT (the caller owns and disposes it).</param>
    public ObjectServicesBuilder(RegistryContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ObjectServicesBuilder WithLogger(ILogger<ObjectsManager> logger) => Set(() => _logger = logger);
    public ObjectServicesBuilder WithSettings(IOptions<AppSettings> settings) => Set(() => _settings = settings);
    public ObjectServicesBuilder WithDdbManager(IDdbManager ddbManager) => Set(() => _ddbManager = ddbManager);
    public ObjectServicesBuilder WithDatasets(IDatasetResolver datasets) => Set(() => _datasets = datasets);

    public ObjectServicesBuilder WithQuota(IStorageQuota quota) => Set(() => _quota = quota);
    public ObjectServicesBuilder WithAuthManager(IAuthManager authManager) => Set(() => _authManager = authManager);
    public ObjectServicesBuilder WithCacheManager(ICacheManager cacheManager) => Set(() => _cacheManager = cacheManager);
    public ObjectServicesBuilder WithFileSystem(IFileSystem fileSystem) => Set(() => _fileSystem = fileSystem);
    public ObjectServicesBuilder WithBackgroundJobs(IBackgroundJobsProcessor jobs) => Set(() => _backgroundJobs = jobs);
    public ObjectServicesBuilder WithDdbWrapper(IDdbWrapper ddbWrapper) => Set(() => _ddbWrapper = ddbWrapper);
    public ObjectServicesBuilder WithThumbnailGenerator(IThumbnailGenerator generator) => Set(() => _thumbnailGenerator = generator);
    public ObjectServicesBuilder WithJobIndexQuery(IJobIndexQuery query) => Set(() => _jobIndexQuery = query);

    public ObjectServicesBuilder WithJobIndexWriter(IJobIndexWriter writer) => Set(() => _jobIndexWriter = writer);
    public ObjectServicesBuilder WithBuildPendingService(BuildPendingService service) => Set(() => _buildPendingService = service);
    public ObjectServicesBuilder WithBuildArtifactResolver(IBuildArtifactResolver resolver) => Set(() => _buildArtifactResolver = resolver);
    public ObjectServicesBuilder WithIndexQueue(IDatasetIndexQueue indexQueue) => Set(() => _indexQueue = indexQueue);
    public ObjectServicesBuilder WithNativeGate(INativeWorkGate nativeGate) => Set(() => _nativeGate = nativeGate);

    /// <summary>The facade and the services, sharing the collaborators.</summary>
    public ObjectServices Build()
    {
        var settings = _settings ?? TestSettings.Options();
        var ddbManager = _ddbManager ?? Mock.Of<IDdbManager>();
        var backgroundJobs = _backgroundJobs ?? new SimpleBackgroundJobsProcessor();
        var cacheManager = _cacheManager ?? Mock.Of<ICacheManager>();
        var datasets = _datasets ?? Mock.Of<IDatasetResolver>();
        var quota = _quota ?? Mock.Of<IStorageQuota>();
        var authManager = _authManager ?? Mock.Of<IAuthManager>();
        var fileSystem = _fileSystem ?? new FileSystem();
        var buildPendingService = _buildPendingService ??
                                  TestJobs.BuildPendingService(_context, ddbManager, backgroundJobs, cacheManager);
        var access = new DatasetAccessResolver(datasets, authManager, ddbManager, TestPaths.Policy);
        var thumbnails = TestSettings.Options<ThumbnailOptions>(settings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_logger);
        services.AddSingleton(_context);
        services.AddSingleton(settings);
        services.AddSingleton(thumbnails);
        services.AddSingleton(TestSettings.Options<DownloadOptions>(settings));
        services.AddSingleton(TestSettings.Options<ImportSettings>(settings));
        services.AddSingleton(ddbManager);
        services.AddSingleton(datasets);
        services.AddSingleton(quota);
        services.AddSingleton(authManager);
        services.AddSingleton<IDatasetAccessResolver>(access);
        services.AddSingleton(cacheManager);
        services.AddSingleton(fileSystem);
        services.AddSingleton(backgroundJobs);
        services.AddSingleton(_ddbWrapper ?? Mock.Of<IDdbWrapper>());
        services.AddSingleton(_thumbnailGenerator ?? Mock.Of<IThumbnailGenerator>());
        services.AddSingleton(_jobIndexQuery ?? Mock.Of<IJobIndexQuery>());
        services.AddSingleton(buildPendingService);
        services.AddSingleton(TestPaths.Policy);
        services.AddSingleton(TestScratch.Provider);
        services.AddSingleton<IZipArchiveBuilder>(new ZipArchiveBuilder());
        services.AddSingleton(Mock.Of<IOgcLayerCatalog>());
        services.AddSingleton(Mock.Of<IBuildStatusService>());
        services.AddSingleton(_nativeGate ?? NativeWorkGate.Default);
        var artifacts = _buildArtifactResolver ?? TestPorts.Artifacts();
        var scheduler = new BuildScheduler(artifacts, backgroundJobs, buildPendingService,
            new CurrentUserBuildRequester(authManager), NullLogger<BuildScheduler>.Instance);
        services.AddSingleton<BuildScheduler>(scheduler);
        services.AddSingleton<IUrlBuilder>(new UrlBuilder(TestSettings.Options<HttpHostOptions>(settings),
            new Microsoft.AspNetCore.Http.HttpContextAccessor()));
        services.AddSingleton(_jobIndexWriter ?? Mock.Of<IJobIndexWriter>());
        services.AddSingleton<UploadManager>(TestUploads.Create(authManager, access, fileSystem, thumbnails,
            TestSettings.Options<ImportSettings>(settings), scheduler,
            new UploadQuotaGuard(new UploadQuotaLedger(TestSettings.Options<UploadSettings>(settings)), quota),
            new DatasetChangeCoalescer(TestJobs.OgcCacheKeys(), cacheManager, thumbnails.Value.Candidates,
                TimeSpan.Zero, NullLogger<DatasetChangeCoalescer>.Instance),
            indexQueue: _indexQueue));

        services.AddObjectsSlice();
        services.AddSingleton(artifacts);

        var provider = services.BuildServiceProvider().CreateScope().ServiceProvider;
        return new ObjectServices(
            (ObjectsManager)provider.GetRequiredService<IObjectsManager>(),
            provider.GetRequiredService<IEntryQueryService>(),
            provider.GetRequiredService<IEntryCommandService>(),
            provider.GetRequiredService<IEntryTransferService>(),
            provider.GetRequiredService<IObjectRenderService>(),
            provider.GetRequiredService<BuildQueryService>(),
            provider.GetRequiredService<RasterAnalysisService>(),
            provider.GetRequiredService<RasterProcessingService>());
    }

    private ObjectServicesBuilder Set(Action assign)
    {
        assign();
        return this;
    }
}

/// <summary>The objects slice of a test (<see cref="ObjectServicesBuilder"/>).</summary>
public sealed record ObjectServices(
    ObjectsManager Manager,
    IEntryQueryService Queries,
    IEntryCommandService Commands,
    IEntryTransferService Transfers,
    IObjectRenderService Renders,
    BuildQueryService Builds,
    RasterAnalysisService Raster,
    RasterProcessingService Processing);
