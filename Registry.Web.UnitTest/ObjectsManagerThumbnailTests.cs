#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// The image/tile/raster-info pipeline must answer 404 "not processed yet" — not 500 — when the
/// build artifact a renderer reads does not exist yet. The render source comes from libddb's build
/// manifest (XI-04: the artifact of the entry's own build with a rendered role), is checked BEFORE
/// the response cache, and entries of a type that is never rendered from a build skip the manifest
/// call.
/// </summary>
[TestFixture]
public class ObjectsManagerThumbnailTests : TestBase
{
    private Mock<IUtils> _utilsMock = null!;
    private Mock<IAuthManager> _authManagerMock = null!;
    private Mock<IDdbManager> _ddbManagerMock = null!;
    private Mock<ICacheManager> _cacheManagerMock = null!;
    private Mock<IFileSystem> _fsMock = null!;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock = null!;
    private Mock<IDdbWrapper> _ddbWrapperMock = null!;
    private Mock<IThumbnailGenerator> _thumbsMock = null!;
    private Mock<IJobIndexQuery> _jobIndexQueryMock = null!;
    private IOptions<AppSettings> _settingsOptions = null!;
    private Mock<IDDB> _ddbMock = null!;
    private ILogger<ObjectsManager> _logger = null!;

    private Dataset _ds = null!;

    private const string OrgSlug = "test-org";
    private const string DsSlug = "test-dataset";
    private const string DatasetFolder = "/tmp/datasets/test-org/test-dataset";

    private static readonly byte[] ThumbBytes = { 1, 2, 3, 4 };

    [SetUp]
    public void Setup()
    {
        _utilsMock = new Mock<IUtils>();
        _authManagerMock = new Mock<IAuthManager>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _cacheManagerMock = new Mock<ICacheManager>();
        _fsMock = new Mock<IFileSystem>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _ddbWrapperMock = new Mock<IDdbWrapper>();
        _thumbsMock = new Mock<IThumbnailGenerator>();
        _jobIndexQueryMock = new Mock<IJobIndexQuery>();

        _settingsOptions = TestSettings.Options(new AppSettings { DatasetsPath = "/tmp/datasets", DefaultThumbnailSize = 128 });
        _logger = CreateTestLogger<ObjectsManager>();

        _ddbWrapperMock.SetupGet(w => w.ThumbnailMimeType).Returns("image/webp");

        var org = new Organization { Slug = OrgSlug, Name = "Org", CreationDate = DateTime.UtcNow, OwnerId = "u" };
        _ds = new Dataset
        {
            Slug = DsSlug,
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.UtcNow,
            Organization = org
        };

        _utilsMock.Setup(x => x.GetDatasetAsync(OrgSlug, DsSlug, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_ds);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        _ddbMock = new Mock<IDDB>();
        _ddbMock.SetupGet(x => x.DatasetFolderPath).Returns(DatasetFolder);
        _ddbMock.SetupGet(x => x.BuildFolderPath).Returns(Path.Combine(DatasetFolder, ".ddb", "build"));
        _ddbMock.Setup(x => x.GetLocalPath(It.IsAny<string>()))
            .Returns<string>(p => Path.Combine(DatasetFolder, p.Replace('/', Path.DirectorySeparatorChar)));
        _ddbManagerMock.Setup(x => x.Get(OrgSlug, _ds.InternalRef)).Returns(_ddbMock.Object);

        // Cache passthrough: execute the factory so generator-invocation assertions are meaningful.
        _cacheManagerMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()))
            .ReturnsAsync((string _, string _, object[] parameters) =>
            {
                var factory = parameters.OfType<Func<Task<byte[]>>>().FirstOrDefault();
                return factory!.Invoke().GetAwaiter().GetResult();
            });

        _thumbsMock.Setup(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()))
            .Callback<string, int, Stream>((_, _, output) => output.Write(ThumbBytes))
            .Returns(Task.CompletedTask);
    }

    // The build-pending service is the builder default: a real one on the SUT context and these mocks.
    private ObjectServices CreateManager() =>
        new ObjectServicesBuilder(TestDb.CreateContext<RegistryContext>())
            .WithLogger(_logger)
            .WithSettings(_settingsOptions)
            .WithDdbManager(_ddbManagerMock.Object)
            .WithUtils(_utilsMock.Object)
            .WithAuthManager(_authManagerMock.Object)
            .WithCacheManager(_cacheManagerMock.Object)
            .WithFileSystem(_fsMock.Object)
            .WithBackgroundJobs(_backgroundJobMock.Object)
            .WithDdbWrapper(_ddbWrapperMock.Object)
            .WithThumbnailGenerator(_thumbsMock.Object)
            .WithJobIndexQuery(_jobIndexQueryMock.Object)
            .Build();

    private void SetupEntry(string path, EntryType type, string hash)
    {
        var entry = new Entry { Path = path, Type = type, Hash = hash, Size = 100 };
        _ddbMock.Setup(x => x.Search(path, It.IsAny<bool>())).Returns(new[] { entry });
        _ddbMock.Setup(x => x.GetEntry(path)).Returns(entry);
    }

    private static bool IsCopcArtifact(string localPath) =>
        localPath.Replace('\\', '/').EndsWith(".ddb/build/abc123/copc/cloud.copc.laz");

    // libddb's build manifest of the entry: its state and the artifacts that exist
    private void SetupBuild(string path, EntryType type, string hash, BuildState state, params BuildArtifact[] artifacts) =>
        _ddbMock.SetupBuilds(TestBuilds.Build(DatasetFolder, path, type, hash, state, artifacts));

    private static BuildArtifact Copc => TestBuilds.File(ArtifactRoles.PointCloudCopc, "copc/cloud.copc.laz");

    [Test]
    public async Task GenerateThumbnailData_PointCloud_MissingCopcArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        SetupBuild("data/cloud.laz", EntryType.PointCloud, "abc123", BuildState.None);

        var mgr = CreateManager();

        var ex = await Should.ThrowAsync<NotFoundException>(
            () => mgr.Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "data/cloud.laz", null));

        ex.Message.ShouldContain("not available yet");
        // Pre-cache guard: one manifest call, nothing was generated or cached
        _ddbMock.Verify(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Once);
        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailData_PointCloud_ArtifactPresent_GeneratesThumbnail()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        SetupBuild("data/cloud.laz", EntryType.PointCloud, "abc123", BuildState.Complete, Copc);

        var mgr = CreateManager();
        var result = await mgr.Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "data/cloud.laz", null);

        result.ShouldNotBeNull();
        result.Data.ShouldBe(ThumbBytes);
        result.ContentType.ShouldBe("image/webp");
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.Is<string>(p => IsCopcArtifact(p)), It.IsAny<int>(),
            It.IsAny<Stream>()), Times.Once);
    }

    [Test]
    public async Task GenerateThumbnailData_TypeNeverRenderedFromABuild_SkipsTheManifest()
    {
        SetupEntry("photo.jpg", EntryType.GeoImage, "img1");

        var mgr = CreateManager();
        var result = await mgr.Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "photo.jpg", null);
        result.ShouldNotBeNull();

        // GeoImage has no rendered build role (libddb's entry types): no manifest call, the
        // thumbnail is generated from the file itself
        result.Data.ShouldBe(ThumbBytes);
        _ddbMock.Verify(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()), Times.Once);
    }

    [Test]
    public async Task GenerateThumbnailData_FolderPath_IsRejectedInsteadOfFirstChild()
    {
        // DDB-11 B5: the folder listing's first child used to be thumbnailed in its place
        var folder = new Entry { Path = "photos", Type = EntryType.Directory };
        var child = new Entry { Path = "photos/a.jpg", Type = EntryType.GeoImage, Hash = "img1", Size = 100 };
        _ddbMock.Setup(x => x.Search("photos", It.IsAny<bool>())).Returns(new[] { child });
        _ddbMock.Setup(x => x.GetEntry("photos")).Returns(folder);

        var ex = await Should.ThrowAsync<BadRequestException>(
            () => CreateManager().Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "photos", null));

        ex.Message.ShouldContain("folder");
        _ddbMock.Verify(x => x.Search(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailData_MissingPath_IsInvalid()
    {
        _ddbMock.Setup(x => x.GetEntry("missing.jpg")).Returns((Entry?)null);

        await Should.ThrowAsync<BadRequestException>(
            () => CreateManager().Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "missing.jpg", null));

        _ddbMock.Verify(x => x.GetEntry("missing.jpg"), Times.Once);
        _ddbMock.Verify(x => x.Search(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailData_Recreate_RemovesTheKeyItReads()
    {
        SetupEntry("photo.jpg", EntryType.GeoImage, "img1");
        object[]? removed = null, read = null;
        _cacheManagerMock.Setup(x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, string, object[]>((_, _, p) => removed = p)
            .Returns(Task.CompletedTask);
        _cacheManagerMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, string, object[]>((_, _, p) => read = p)
            .ReturnsAsync(ThumbBytes);

        await CreateManager().Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "photo.jpg", 256, recreate: true);

        removed.ShouldNotBeNull();
        read.ShouldNotBeNull();
        // Delegates are not part of the cache key.
        removed.ShouldBe(read.Where(p => p is not Delegate).ToArray());
    }

    [Test]
    public async Task GenerateTileData_GeoRaster_MissingCogArtifact_ThrowsNotFound_BeforeCache()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.Pending);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Renders.GenerateTileDataAsync(OrgSlug, DsSlug, "ortho.tif", 1, 0, 0, false));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
        _ddbMock.Verify(x => x.GenerateTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailDataEx_PointCloud_MissingArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        SetupBuild("data/cloud.laz", EntryType.PointCloud, "abc123", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Renders.GenerateThumbnailDataExAsync(OrgSlug, DsSlug, "data/cloud.laz", null, new RasterVisualization()));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Test]
    public async Task GenerateTileDataEx_PointCloud_MissingArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        SetupBuild("data/cloud.laz", EntryType.PointCloud, "abc123", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Renders.GenerateTileDataExAsync(OrgSlug, DsSlug, "data/cloud.laz", 1, 0, 0, false, new RasterVisualization()));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Test]
    public async Task GetRasterInfo_GeoRaster_MissingCogArtifact_ThrowsNotFound()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(() => mgr.Raster.GetRasterInfoAsync(OrgSlug, DsSlug, "ortho.tif"));

        _ddbMock.Verify(x => x.GetRasterInfo(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetRasterInfo_PointCloud_LegacyEptFallback_UsesEptArtifact()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        _ddbMock.Setup(x => x.GetRasterInfo(It.IsAny<string>())).Returns("{}");
        // libddb lists the legacy EPT output only when there is no COPC: it is read through its
        // descriptor, not 404'd as a missing build
        SetupBuild("data/cloud.laz", EntryType.PointCloud, "abc123", BuildState.None,
            TestBuilds.Directory(ArtifactRoles.PointCloudEpt, "ept", "ept.json", legacy: true));

        var mgr = CreateManager();
        var json = await mgr.Raster.GetRasterInfoAsync(OrgSlug, DsSlug, "data/cloud.laz");

        json.ShouldBe("{}");
        _ddbMock.Verify(x => x.GetRasterInfo(".ddb/build/abc123/ept/ept.json"), Times.Once);
    }

    [Test]
    public async Task ExportRaster_GeoRaster_MissingCogArtifact_ThrowsNotFound()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Raster.ExportRasterAsync(OrgSlug, DsSlug, "ortho.tif", new RasterVisualization()));

        _ddbMock.Verify(x => x.ExportRaster(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task EstimateExportSize_GeoRaster_MissingCogArtifact_ThrowsNotFound()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Raster.EstimateExportSizeAsync(OrgSlug, DsSlug, "ortho.tif"));

        _ddbMock.Verify(x => x.GetRasterInfo(It.IsAny<string>()), Times.Never);
    }

    // ---- Raster analysis endpoint family: same pre-native 404 guard ----

    private async Task GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
        Func<ObjectServices, Task> invoke, Expression<Action<IDDB>> nativeCall)
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(() => invoke(mgr));

        // One manifest call, and the native call is never reached
        _ddbMock.Verify(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Once);
        _ddbMock.Verify(nativeCall, Times.Never);
        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Test]
    public Task GetRasterValueInfo_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif"),
            x => x.GetRasterValueInfo(It.IsAny<string>()));

    [Test]
    public Task GetRasterPointValue_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 1, 2),
            x => x.GetRasterPointValue(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()));

    [Test]
    public Task GetRasterAreaStats_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.GetRasterAreaStatsAsync(OrgSlug, DsSlug, "ortho.tif", 0, 0, 10, 10),
            x => x.GetRasterAreaStats(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>()));

    [Test]
    public Task GetRasterProfile_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.GetRasterProfileAsync(OrgSlug, DsSlug, "ortho.tif", "LINESTRING(0 0, 1 1)", 10),
            x => x.GetRasterProfile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()));

    [Test]
    public Task CalculateVolume_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.CalculateVolumeAsync(OrgSlug, DsSlug, "ortho.tif", "{}", null, 0.0),
            x => x.CalculateVolume(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()));

    [Test]
    public Task DetectStockpile_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.DetectStockpileAsync(OrgSlug, DsSlug, "ortho.tif", 0.0, 0.0, 5.0, 0.5f),
            x => x.DetectStockpile(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<float>()));

    [Test]
    public Task DetectAllStockpiles_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.DetectAllStockpilesAsync(OrgSlug, DsSlug, "ortho.tif", 0.5f, 10.0, 50),
            x => x.DetectAllStockpiles(It.IsAny<string>(), It.IsAny<float>(), It.IsAny<double>(),
                It.IsAny<int>()));

    [Test]
    public Task GenerateContours_GeoRaster_MissingCogArtifact_ThrowsNotFound() =>
        GeoRasterAnalysisGuard_MissingCog_ThrowsNotFound(
            m => m.Raster.GenerateContoursAsync(OrgSlug, DsSlug, "ortho.tif", new ContourOptions(1.0, null, 0.0, null, null, 0.0, 0)),
            x => x.GenerateContours(It.IsAny<string>(), It.IsAny<double?>(), It.IsAny<int?>(),
                It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<double>(),
                It.IsAny<int>()));
}
