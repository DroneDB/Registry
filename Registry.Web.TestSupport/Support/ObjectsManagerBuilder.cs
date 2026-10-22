#nullable enable
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Registry.Adapters;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Adapters;

namespace Registry.Web.Test.Support;

/// <summary>
/// Builds an <see cref="ObjectsManager"/> for tests, so a test names only the dependencies it cares
/// about. Every other dependency gets a default:
/// <list type="bullet">
/// <item>settings: <see cref="TestSettings.Options(Action{AppSettings}?)"/>; logger: null logger;</item>
/// <item>file system: the real <see cref="FileSystem"/>; background jobs: <see cref="SimpleBackgroundJobsProcessor"/>;</item>
/// <item>DDB manager, utils, auth, cache, DDB wrapper, thumbnails, job index query: loose Moq mocks;</item>
/// <item>build-pending service: a real <see cref="BuildPendingService"/> on the SUT context and the
/// DDB manager, background jobs and cache above (resolved at <see cref="Build"/> time);</item>
/// <item>dataset cache invalidator: a real one over the cache above (<see cref="TestJobs.CacheInvalidator"/>);</item>
/// <item>the other optional constructor dependencies: <c>null</c>, so the manager uses its own fallbacks.</item>
/// </list>
/// </summary>
public sealed class ObjectsManagerBuilder
{
    private readonly RegistryContext _context;

    private ILogger<ObjectsManager> _logger = NullLogger<ObjectsManager>.Instance;
    private IOptions<AppSettings>? _settings;
    private IDdbManager? _ddbManager;
    private IUtils? _utils;
    private IAuthManager? _authManager;
    private ICacheManager? _cacheManager;
    private IFileSystem? _fileSystem;
    private IBackgroundJobsProcessor? _backgroundJobs;
    private IDdbWrapper? _ddbWrapper;
    private IThumbnailGenerator? _thumbnailGenerator;
    private IJobIndexQuery? _jobIndexQuery;
    private BuildPendingService? _buildPendingService;
    private IZipArchiveBuilder? _zipArchiveBuilder;
    private IOgcLayerCatalog? _ogcLayerCatalog;
    private IDatasetCacheInvalidator? _datasetCacheInvalidator;
    private IBuildArtifactResolver? _buildArtifactResolver;
    private IBuildStatusService? _buildStatusService;
    private IDatasetIndexQueue? _indexQueue;
    private IUploadManager? _uploadManager;
    private INativeWorkGate? _nativeGate;

    /// <param name="context">The registry context of the SUT (the caller owns and disposes it).</param>
    public ObjectsManagerBuilder(RegistryContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ObjectsManagerBuilder WithLogger(ILogger<ObjectsManager> logger) => Set(() => _logger = logger);
    public ObjectsManagerBuilder WithSettings(IOptions<AppSettings> settings) => Set(() => _settings = settings);
    public ObjectsManagerBuilder WithDdbManager(IDdbManager ddbManager) => Set(() => _ddbManager = ddbManager);
    public ObjectsManagerBuilder WithUtils(IUtils utils) => Set(() => _utils = utils);
    public ObjectsManagerBuilder WithAuthManager(IAuthManager authManager) => Set(() => _authManager = authManager);
    public ObjectsManagerBuilder WithCacheManager(ICacheManager cacheManager) => Set(() => _cacheManager = cacheManager);
    public ObjectsManagerBuilder WithFileSystem(IFileSystem fileSystem) => Set(() => _fileSystem = fileSystem);
    public ObjectsManagerBuilder WithBackgroundJobs(IBackgroundJobsProcessor jobs) => Set(() => _backgroundJobs = jobs);
    public ObjectsManagerBuilder WithDdbWrapper(IDdbWrapper ddbWrapper) => Set(() => _ddbWrapper = ddbWrapper);
    public ObjectsManagerBuilder WithThumbnailGenerator(IThumbnailGenerator generator) => Set(() => _thumbnailGenerator = generator);
    public ObjectsManagerBuilder WithJobIndexQuery(IJobIndexQuery query) => Set(() => _jobIndexQuery = query);
    public ObjectsManagerBuilder WithBuildPendingService(BuildPendingService service) => Set(() => _buildPendingService = service);
    public ObjectsManagerBuilder WithZipArchiveBuilder(IZipArchiveBuilder builder) => Set(() => _zipArchiveBuilder = builder);
    public ObjectsManagerBuilder WithOgcLayerCatalog(IOgcLayerCatalog catalog) => Set(() => _ogcLayerCatalog = catalog);
    public ObjectsManagerBuilder WithDatasetCacheInvalidator(IDatasetCacheInvalidator invalidator) => Set(() => _datasetCacheInvalidator = invalidator);
    public ObjectsManagerBuilder WithBuildArtifactResolver(IBuildArtifactResolver resolver) => Set(() => _buildArtifactResolver = resolver);
    public ObjectsManagerBuilder WithBuildStatusService(IBuildStatusService service) => Set(() => _buildStatusService = service);
    public ObjectsManagerBuilder WithIndexQueue(IDatasetIndexQueue indexQueue) => Set(() => _indexQueue = indexQueue);
    public ObjectsManagerBuilder WithUploadManager(IUploadManager uploadManager) => Set(() => _uploadManager = uploadManager);
    public ObjectsManagerBuilder WithNativeGate(INativeWorkGate nativeGate) => Set(() => _nativeGate = nativeGate);

    public ObjectsManager Build()
    {
        var ddbManager = _ddbManager ?? Mock.Of<IDdbManager>();
        var backgroundJobs = _backgroundJobs ?? new SimpleBackgroundJobsProcessor();
        var cacheManager = _cacheManager ?? Mock.Of<ICacheManager>();
        var buildPendingService = _buildPendingService ??
                                  TestJobs.BuildPendingService(_context, ddbManager, backgroundJobs, cacheManager);

        var utils = _utils ?? Mock.Of<IUtils>();
        var authManager = _authManager ?? Mock.Of<IAuthManager>();

        return new ObjectsManager(
            _logger,
            _context,
            _settings ?? TestSettings.Options(),
            ddbManager,
            utils,
            authManager,
            new DatasetAccessResolver(utils, authManager, ddbManager, TestPaths.Policy),
            cacheManager,
            _fileSystem ?? new FileSystem(),
            backgroundJobs,
            _ddbWrapper ?? Mock.Of<IDdbWrapper>(),
            _thumbnailGenerator ?? Mock.Of<IThumbnailGenerator>(),
            _jobIndexQuery ?? Mock.Of<IJobIndexQuery>(),
            buildPendingService,
            TestPaths.Policy,
            TestScratch.Provider,
            _zipArchiveBuilder,
            _ogcLayerCatalog,
            _datasetCacheInvalidator ?? TestJobs.CacheInvalidator(cacheManager),
            _buildArtifactResolver ?? new BuildArtifactResolver(new FakeDdbWrapper()),
            _buildStatusService,
            _indexQueue,
            _uploadManager,
            _nativeGate);
    }

    private ObjectsManagerBuilder Set(Action assign)
    {
        assign();
        return this;
    }
}
