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
/// manifest (the artifact of the entry's own build with a rendered role), is checked BEFORE
/// the response cache, and entries of a type that is never rendered from a build skip the manifest
/// call.
/// </summary>
[TestFixture]
public class ObjectsManagerThumbnailTests : TestBase
{
    private Mock<IDatasetResolver> _utilsMock = null!;
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
        _utilsMock = new Mock<IDatasetResolver>();
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
        _ddbMock.SetupGet(x => x.DerivedFolderPaths).Returns([Path.Combine(DatasetFolder, ".ddb", "build")]);
        _ddbMock.Setup(x => x.GetLocalPath(It.IsAny<string>()))
            .Returns<string>(p => Path.Combine(DatasetFolder, p.Replace('/', Path.DirectorySeparatorChar)));
        _ddbManagerMock.Setup(x => x.Get(OrgSlug, _ds.InternalRef)).Returns(_ddbMock.Object);

        // Cache passthrough: execute the factory so generator-invocation assertions are meaningful.
        _cacheManagerMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<object[]>()))
            .ReturnsAsync((string _, string _, CancellationToken ct, object[] parameters) =>
            {
                var factory = parameters.OfType<Func<CancellationToken, Task<byte[]>>>().FirstOrDefault();
                return factory!.Invoke(ct).GetAwaiter().GetResult();
            });

        _thumbsMock.Setup(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<string, int, Stream, CancellationToken>((_, _, output, _) => output.Write(ThumbBytes))
            .Returns(Task.CompletedTask);
    }

    // The build-pending service is the builder default: a real one on the SUT context and these mocks.
    private ObjectServices CreateManager() =>
        new ObjectServicesBuilder(TestDb.CreateContext<RegistryContext>())
            .WithLogger(_logger)
            .WithSettings(_settingsOptions)
            .WithDdbManager(_ddbManagerMock.Object)
            .WithDatasets(_utilsMock.Object)
            .WithAuthManager(_authManagerMock.Object)
            .WithCacheManager(_cacheManagerMock.Object)
            .WithFileSystem(_fsMock.Object)
            .WithBackgroundJobs(_backgroundJobMock.Object)
            .WithDdbWrapper(_ddbWrapperMock.Object)
            .WithThumbnailGenerator(_thumbsMock.Object)
            .WithJobIndexQuery(_jobIndexQueryMock.Object)
            .Build();

    private void SetupEntry(string path, EntryType type, string hash, long? width = null, long? height = null)
    {
        var entry = new Entry { Path = path, Type = type, Hash = hash, Size = 100 };
        // The size libddb's probe of the file put in the index
        if (width is { } w && height is { } h)
            entry.Properties = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["width"] = System.Text.Json.JsonSerializer.SerializeToElement(w),
                ["height"] = System.Text.Json.JsonSerializer.SerializeToElement(h)
            };
        _ddbMock.Setup(x => x.GetEntry(path)).Returns(entry);
    }

    private static bool IsCopcArtifact(string localPath) =>
        localPath.Replace('\\', '/').EndsWith(".ddb/build/abc123/copc/cloud.copc.laz", StringComparison.Ordinal);

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
        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Never);
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GenerateThumbnailData_FolderPath_IsRejectedInsteadOfFirstChild()
    {
        // B5: the folder listing's first child used to be thumbnailed in its place
        var folder = new Entry { Path = "photos", Type = EntryType.Directory };
        var child = new Entry { Path = "photos/a.jpg", Type = EntryType.GeoImage, Hash = "img1", Size = 100 };
        _ddbMock.Setup(x => x.GetEntry("photos")).Returns(folder);

        var ex = await Should.ThrowAsync<BadRequestException>(
            () => CreateManager().Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "photos", null));

        ex.Message.ShouldContain("folder");
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailData_MissingPath_IsInvalid()
    {
        _ddbMock.Setup(x => x.GetEntry("missing.jpg")).Returns((Entry?)null);

        await Should.ThrowAsync<BadRequestException>(
            () => CreateManager().Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "missing.jpg", null));

        _ddbMock.Verify(x => x.GetEntry("missing.jpg"), Times.Once);
    }

    [Test]
    public async Task GenerateThumbnailData_Recreate_RemovesTheKeyItReads()
    {
        SetupEntry("photo.jpg", EntryType.GeoImage, "img1");
        object[]? removed = null, read = null;
        _cacheManagerMock.Setup(x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()))
            .Callback<string, string, object[]>((_, _, p) => removed = p)
            .Returns(Task.CompletedTask);
        _cacheManagerMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<object[]>()))
            .Callback<string, string, CancellationToken, object[]>((_, _, _, p) => read = p)
            .ReturnsAsync(ThumbBytes);

        await CreateManager().Renders.GenerateThumbnailDataAsync(OrgSlug, DsSlug, "photo.jpg", 256, recreate: true);

        removed.ShouldNotBeNull();
        read.ShouldNotBeNull();
        // Delegates are not part of the cache key.
        removed.ShouldBe(read.Where(p => p is not Delegate).ToArray());
    }

    [Test]
    public async Task ResolveReadableObject_GeoRaster_MissingCogArtifact_ThrowsNotFound_BeforeCache()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.Pending);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Renders.ResolveReadableObjectAsync(OrgSlug, DsSlug, "ortho.tif"));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Never);
        _ddbMock.Verify(x => x.GenerateTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailDataEx_PointCloud_MissingArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        SetupBuild("data/cloud.laz", EntryType.PointCloud, "abc123", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Renders.GenerateThumbnailDataExAsync(OrgSlug, DsSlug, "data/cloud.laz", null, new RasterVisualization()));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Never);
    }

    [Test]
    public async Task GenerateTileDataEx_PointCloud_MissingArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        SetupBuild("data/cloud.laz", EntryType.PointCloud, "abc123", BuildState.None);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.Renders.GenerateTileDataExAsync(OrgSlug, DsSlug, "data/cloud.laz", 1, 0, 0, false, new RasterVisualization(), TileFormat.Webp));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Never);
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
        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()), Times.Never);
    }

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

    // ---- The raster values are read from the source file of the entry, never from the resampled COG (D-021) ----

    private async Task ValuesAreReadFromTheSourceFile(Func<ObjectServices, Task> invoke, Expression<Action<IDDB>> nativeCall)
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        // The entry has a build that has not produced its COG yet: the values do not wait for it
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.None);
        _ddbMock.Setup(x => x.GetRasterValueInfo(It.IsAny<string>(), It.IsAny<RasterAnalysisOptions?>())).Returns("{}");

        await invoke(CreateManager());

        _ddbMock.Verify(nativeCall, Times.Once);
        _ddbMock.Verify(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Test]
    public Task GetRasterValueInfo_ReadsTheSourceFile() =>
        ValuesAreReadFromTheSourceFile(
            m => m.Raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif"),
            x => x.GetRasterValueInfo("ortho.tif", It.IsAny<RasterAnalysisOptions?>()));

    [Test]
    public Task GetRasterPointValue_AtAPoint_ReadsTheSourceFile() =>
        ValuesAreReadFromTheSourceFile(
            m => m.Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 12.5, 45.5),
            x => x.GetRasterPointValue("ortho.tif", 12.5, 45.5, It.IsAny<RasterAnalysisOptions?>()));

    [Test]
    public async Task GetRasterPointValue_AtAPixelOfAnRJpeg_ReadsTheSourceFileWithTheOptions()
    {
        SetupEntry("thermal.jpg", EntryType.GeoImage, "rjpeg1");
        RasterAnalysisOptions? seen = null;
        _ddbMock.Setup(x => x.GetRasterPointValue("thermal.jpg", 1, 2, It.IsAny<RasterAnalysisOptions?>()))
            .Callback<string, int, int, RasterAnalysisOptions?>((_, _, _, o) => seen = o).Returns("{}");

        await CreateManager().Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "thermal.jpg", 1, 2,
            new RasterAnalysisOptions(Nodata: -9999, Formula: "CELSIUS"));

        seen.ShouldNotBeNull();
        seen.Nodata.ShouldBe(-9999);
        seen.Formula.ShouldBe("CELSIUS");
    }

    [Test]
    public async Task GetRasterAreaStats_OfPixelsOfAnRJpeg_ReadsTheSourceFileWithTheOptions()
    {
        SetupEntry("thermal.jpg", EntryType.GeoImage, "rjpeg1");
        RasterAnalysisOptions? seen = null;
        _ddbMock.Setup(x => x.GetRasterAreaStats("thermal.jpg", 0, 0, 5, 5, It.IsAny<RasterAnalysisOptions?>()))
            .Callback<string, int, int, int, int, RasterAnalysisOptions?>((_, _, _, _, _, o) => seen = o).Returns("{}");

        await CreateManager().Raster.GetRasterAreaStatsAsync(OrgSlug, DsSlug, "thermal.jpg", 0, 0, 5, 5,
            new RasterAnalysisOptions(Nodata: 0, Formula: "KELVIN"));

        seen.ShouldNotBeNull();
        seen.Nodata.ShouldBe(0);
        seen.Formula.ShouldBe("KELVIN");
    }

    // Pixel coordinates are for an R-JPEG's raw thermal grid only (§7.1): a georeferenced raster takes map coordinates
    [Test]
    public async Task PixelCoordinatesOnAGeoreferencedRaster_AreABadRequestOfTheField()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");

        var point = await Should.ThrowAsync<InvalidFieldException>(() =>
            CreateManager().Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 1, 2));
        var area = await Should.ThrowAsync<InvalidFieldException>(() =>
            CreateManager().Raster.GetRasterAreaStatsAsync(OrgSlug, DsSlug, "ortho.tif", 0, 0, 5, 5));

        point.Field.ShouldBe("x");
        point.Message.ShouldBe(
            "Pixel coordinates are for R-JPEG images only: 'ortho.tif' is a georeferenced raster, send lon, lat (WGS 84)");
        area.Field.ShouldBe("x0");
        area.Message.ShouldBe(
            "Pixel coordinates are for R-JPEG images only: 'ortho.tif' is a georeferenced raster, send bbox (WGS 84)");
        _ddbMock.Verify(x => x.GetRasterPointValue(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<RasterAnalysisOptions?>()), Times.Never);
        _ddbMock.Verify(x => x.GetRasterAreaStats(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<RasterAnalysisOptions?>()), Times.Never);
    }

    [Test]
    public Task GetRasterAreaStats_OfAWindow_ReadsTheSourceFile() =>
        ValuesAreReadFromTheSourceFile(
            m => m.Raster.GetRasterAreaStatsAsync(OrgSlug, DsSlug, "ortho.tif", [12.0, 45.0, 13.0, 46.0]),
            x => x.GetRasterAreaStats("ortho.tif", 12.0, 45.0, 13.0, 46.0, It.IsAny<RasterAnalysisOptions?>()));

    [Test]
    public Task GetRasterProfile_ReadsTheSourceFile() =>
        ValuesAreReadFromTheSourceFile(
            m => m.Raster.GetRasterProfileAsync(OrgSlug, DsSlug, "ortho.tif", "{}", 10),
            x => x.GetRasterProfile("ortho.tif", "{}", 10, It.IsAny<RasterAnalysisOptions?>()));

    [Test]
    public Task GenerateContours_ReadsTheSourceFile() =>
        ValuesAreReadFromTheSourceFile(
            m => m.Raster.GenerateContoursAsync(OrgSlug, DsSlug, "ortho.tif", new ContourOptions(1.0, null, 0.0, null, null, 0.0, 1)),
            x => x.GenerateContours("ortho.tif", 1.0, null, 0.0, null, null, 0.0, 1, It.IsAny<RasterAnalysisOptions?>()));

    [Test]
    public async Task TheValueOptions_ReachLibddb()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        RasterAnalysisOptions? seen = null;
        _ddbMock.Setup(x => x.GetRasterPointValue("ortho.tif", 12.5, 45.5, It.IsAny<RasterAnalysisOptions?>()))
            .Callback<string, double, double, RasterAnalysisOptions?>((_, _, _, o) => seen = o).Returns("{}");

        await CreateManager().Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 12.5, 45.5,
            new RasterAnalysisOptions(Nodata: 0, Formula: "CELSIUS"));

        seen.ShouldNotBeNull();
        seen.Nodata.ShouldBe(0);
        seen.Formula.ShouldBe("CELSIUS");
    }

    // A raster of more pixels than libddb reads in one piece (1 gigapixel) is read from the COG of its build: the size the
    // index holds for the entry decides it before any read, whatever libddb's message
    [Test]
    public async Task ARasterOfMoreThanAGigapixel_IsReadFromItsCogWithoutReadingTheSource()
    {
        // 50,000 x 40,000 = 2,000,000,000 pixels
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1", 50_000, 40_000);
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.Complete,
            TestBuilds.File(ArtifactRoles.RasterCog, "cog/cog.tif"));
        _ddbMock.Setup(x => x.GetRasterPointValue(".ddb/build/raster1/cog/cog.tif", 12.5, 45.5,
            It.IsAny<RasterAnalysisOptions?>())).Returns("{\"value\": 3}");
        _ddbMock.Setup(x => x.GetRasterValueInfo(".ddb/build/raster1/cog/cog.tif", It.IsAny<RasterAnalysisOptions?>()))
            .Returns("""{"pixelCount": 100, "validPixelCount": 100}""");

        var json = await CreateManager().Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 12.5, 45.5);
        var info = await CreateManager().Raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif");

        json.ShouldBe("{\"value\": 3}");
        using var doc = System.Text.Json.JsonDocument.Parse(info);
        doc.RootElement.GetProperty("estimatedExportBytes").GetInt64().ShouldBe(400);
        _ddbMock.Verify(x => x.GetRasterPointValue("ortho.tif", It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<RasterAnalysisOptions?>()), Times.Never);
        _ddbMock.Verify(x => x.GetRasterValueInfo("ortho.tif", It.IsAny<RasterAnalysisOptions?>()), Times.Never);
    }

    // 1,000,000,000 pixels exactly is still read in one piece (libddb refuses more than its limit)
    [Test]
    public async Task ARasterOfAGigapixel_IsReadFromTheSource()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1", 40_000, 25_000);
        _ddbMock.Setup(x => x.GetRasterPointValue("ortho.tif", 12.5, 45.5, It.IsAny<RasterAnalysisOptions?>()))
            .Returns("{\"value\": 1}");

        var json = await CreateManager().Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 12.5, 45.5);

        json.ShouldBe("{\"value\": 1}");
        _ddbMock.Verify(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    // An entry indexed without its size: libddb's refusal is the fallback
    [Test]
    public async Task ARasterTooLargeToRead_WithoutItsSizeInTheIndex_IsReadFromItsCog()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.Complete,
            TestBuilds.File(ArtifactRoles.RasterCog, "cog/cog.tif"));
        _ddbMock.Setup(x => x.GetRasterPointValue("ortho.tif", 12.5, 45.5, It.IsAny<RasterAnalysisOptions?>()))
            .Throws(new InvalidOperationException("Could not read the raster value.",
                new DdbException("2000000000 pixels, more than the 1000000000 that can be read in one piece: build the dataset first")));
        _ddbMock.Setup(x => x.GetRasterPointValue(".ddb/build/raster1/cog/cog.tif", 12.5, 45.5,
            It.IsAny<RasterAnalysisOptions?>())).Returns("{\"value\": 3}");

        var json = await CreateManager().Raster.GetRasterPointValueAsync(OrgSlug, DsSlug, "ortho.tif", 12.5, 45.5);

        json.ShouldBe("{\"value\": 3}");
    }

    // The size of the export and the limit come with the value info (D-023): the pixels that are not NoData x 4 bytes
    [Test]
    public async Task GetRasterValueInfo_AddsTheEstimateAndTheLimitOfAnExport()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        _ddbMock.Setup(x => x.GetRasterValueInfo("ortho.tif", It.IsAny<RasterAnalysisOptions?>()))
            .Returns("""{"width": 100, "height": 100, "pixelCount": 10000, "validPixelCount": 7500}""");

        var json = await CreateManager().Raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("estimatedExportBytes").GetInt64().ShouldBe(30000);
        doc.RootElement.GetProperty("exportLimitBytes").GetInt64().ShouldBe(1073741824);
        doc.RootElement.GetProperty("validPixelCount").GetInt64().ShouldBe(7500);
    }

    [Test]
    public async Task GetRasterValueInfo_OfAWindow_EstimatesTheValidFractionOfTheWindow()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        // 25 % of the file has a value; the window has 4000 source pixels: 1000 pixels x 4 bytes
        _ddbMock.Setup(x => x.GetRasterValueInfo("ortho.tif", It.IsAny<RasterAnalysisOptions?>()))
            .Returns("""{"pixelCount": 10000, "validPixelCount": 2500, "windowPixelCount": 4000}""");

        var json = await CreateManager().Raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif",
            new RasterAnalysisOptions(Bbox: [12.0, 45.0, 13.0, 46.0]));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("estimatedExportBytes").GetInt64().ShouldBe(4000);
    }

    [TestCase(10000L, 10000L, null, 40000L)]
    [TestCase(2500L, 10000L, 4000L, 4000L)]
    [TestCase(0L, 10000L, 4000L, 0L)]
    [TestCase(10000L, 0L, 4000L, 16000L)]
    public void ExportEstimate_IsThePixelsThatAreNotNodataTimesFourBytes(long valid, long total, long? window, long expected) =>
        ExportEstimate.Bytes(valid, total, window).ShouldBe(expected);

    // The source and the COG of its build have different grids (the COG is resampled): the check of an export and the
    // estimate of the value info read the same file, so the Hub's "within the limit" is what the export accepts (D-023)
    private void SetupSourceAndCogWithDifferentGrids(string sourceCounts, string cogCounts)
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.Complete,
            TestBuilds.File(ArtifactRoles.RasterCog, "cog/cog.tif"));
        _ddbMock.Setup(x => x.GetRasterValueInfo("ortho.tif", It.IsAny<RasterAnalysisOptions?>())).Returns(sourceCounts);
        _ddbMock.Setup(x => x.GetRasterValueInfo(".ddb/build/raster1/cog/cog.tif", It.IsAny<RasterAnalysisOptions?>()))
            .Returns(cogCounts);
    }

    [Test]
    public async Task Export_OverTheLimitInTheValueInfo_IsRefused_WhenTheCogIsUnder()
    {
        // Source: 300,000,000 valid pixels x 4 = 1,200,000,000 bytes > 1 GiB; the COG: 200,000,000 x 4 = 800,000,000
        SetupSourceAndCogWithDifferentGrids("""{"pixelCount": 400000000, "validPixelCount": 300000000}""",
            """{"pixelCount": 250000000, "validPixelCount": 200000000}""");
        var raster = CreateManager().Raster;

        var info = await raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif", new RasterAnalysisOptions(Nodata: 0));
        var ex = await Should.ThrowAsync<BadRequestException>(() => raster.ExportRasterAsync(OrgSlug, DsSlug, "ortho.tif",
            new RasterVisualization(Nodata: 0)));

        using var doc = System.Text.Json.JsonDocument.Parse(info);
        doc.RootElement.GetProperty("estimatedExportBytes").GetInt64().ShouldBe(1_200_000_000);
        doc.RootElement.GetProperty("exportLimitBytes").GetInt64().ShouldBe(1_073_741_824);
        ex.Message.ShouldContain("exceeds maximum allowed size");
        _ddbMock.Verify(x => x.GetRasterValueInfo(".ddb/build/raster1/cog/cog.tif", It.IsAny<RasterAnalysisOptions?>()),
            Times.Never);
    }

    [Test]
    public async Task TheCheckOfTheExportTask_IsTheCheckOfTheExport()
    {
        // FR-RAS-16: the submit of raster-export (IRasterExportSizeCheck) refuses what the direct export refuses
        SetupSourceAndCogWithDifferentGrids("""{"pixelCount": 400000000, "validPixelCount": 300000000}""",
            """{"pixelCount": 250000000, "validPixelCount": 200000000}""");
        var raster = CreateManager().Raster;

        var ex = await Should.ThrowAsync<BadRequestException>(() =>
            raster.EnsureExportSizeWithinLimitAsync(OrgSlug, DsSlug, "ortho.tif", RasterAnalysisOptions.None));

        ex.Message.ShouldContain("exceeds maximum allowed size");
        _ddbMock.Verify(x => x.GetRasterValueInfo("ortho.tif", null), Times.Once);
    }

    [Test]
    public async Task Export_UnderTheLimitInTheValueInfo_IsAccepted_WhenTheCogIsOver()
    {
        // Source: 200,000,000 x 4 = 800,000,000 bytes; the COG: 300,000,000 x 4 = 1,200,000,000 > 1 GiB
        SetupSourceAndCogWithDifferentGrids("""{"pixelCount": 250000000, "validPixelCount": 200000000}""",
            """{"pixelCount": 400000000, "validPixelCount": 300000000}""");
        _fsMock.Setup(x => x.ReadAllBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2]);
        var raster = CreateManager().Raster;

        var info = await raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif");
        var export = await raster.ExportRasterAsync(OrgSlug, DsSlug, "ortho.tif", new RasterVisualization());

        using var doc = System.Text.Json.JsonDocument.Parse(info);
        doc.RootElement.GetProperty("estimatedExportBytes").GetInt64().ShouldBe(800_000_000);
        export.Data.ShouldBe(new byte[] { 1, 2 });
        _ddbMock.Verify(x => x.ExportRaster(".ddb/build/raster1/cog/cog.tif", It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Test]
    public async Task TheCheckOfAnExport_SendsTheNodataValue_AndNotTheFormula()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        SetupBuild("ortho.tif", EntryType.GeoRaster, "raster1", BuildState.Complete,
            TestBuilds.File(ArtifactRoles.RasterCog, "cog/cog.tif"));
        RasterAnalysisOptions? asked = null;
        _ddbMock.Setup(x => x.GetRasterValueInfo("ortho.tif", It.IsAny<RasterAnalysisOptions?>()))
            .Callback<string, RasterAnalysisOptions?>((_, o) => asked = o)
            .Returns("""{"pixelCount": 400000000, "validPixelCount": 300000000}""");

        await Should.ThrowAsync<BadRequestException>(() => CreateManager().Raster.ExportRasterAsync(OrgSlug, DsSlug,
            "ortho.tif", new RasterVisualization(Formula: "NDVI", Nodata: 0)));

        asked.ShouldNotBeNull();
        asked.Nodata.ShouldBe(0);
        asked.Formula.ShouldBeNull();
        asked.Bbox.ShouldBeNull();
    }

    // The estimate of a window (the visible extent): the same function, with the window, as the value info of that window
    [Test]
    public async Task TheEstimateOfAWindow_IsTheOneOfTheValueInfoOfTheWindow()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        double[] window = [12.0, 45.0, 13.0, 46.0];
        var asked = new List<RasterAnalysisOptions?>();
        // 25 % of the file has a value; the window has 4000 source pixels: 1000 x 4 bytes. The whole file: 10,000 bytes
        _ddbMock.Setup(x => x.GetRasterValueInfo("ortho.tif", It.IsAny<RasterAnalysisOptions?>()))
            .Callback<string, RasterAnalysisOptions?>((_, o) => asked.Add(o))
            .Returns<string, RasterAnalysisOptions?>((_, o) => o?.Bbox is null
                ? """{"pixelCount": 10000, "validPixelCount": 2500}"""
                : """{"pixelCount": 10000, "validPixelCount": 2500, "windowPixelCount": 4000}""");
        var raster = CreateManager().Raster;

        var estimate = await raster.EstimateExportBytesAsync(OrgSlug, DsSlug, "ortho.tif",
            new RasterAnalysisOptions(Nodata: 0, Formula: "NDVI", Bbox: window));
        var info = await raster.GetRasterValueInfoAsync(OrgSlug, DsSlug, "ortho.tif",
            new RasterAnalysisOptions(Nodata: 0, Bbox: window));
        var whole = await raster.EstimateExportBytesAsync(OrgSlug, DsSlug, "ortho.tif", null);

        estimate.ShouldBe(4000);
        using var doc = System.Text.Json.JsonDocument.Parse(info);
        doc.RootElement.GetProperty("estimatedExportBytes").GetInt64().ShouldBe(4000);
        whole.ShouldBe(10000);
        // The estimate sends the NoData value and the window, not the formula (it changes the stretch, not the counts)
        asked[0].ShouldBe(new RasterAnalysisOptions(0, null, window));
        asked[1].ShouldBe(new RasterAnalysisOptions(0, null, window));
        asked[2].ShouldBeNull("the estimate of the whole file sends no options");
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    public void ANodataValueThatIsNotFinite_IsABadRequest(double nodata) =>
        Should.Throw<BadRequestException>(() => RasterOptionRules.RequireNodata(nodata));

    [TestCase("12.5,45.5,13,46", true)]
    [TestCase("12.5,45.5,13", false)]
    [TestCase("a,b,c,d", false)]
    [TestCase("12,45,200,46", false)]
    public void TheWindow_IsFourDegreesInWgs84(string text, bool valid)
    {
        if (valid)
            RasterOptionRules.ParseBbox(text)!.ShouldBe([12.5, 45.5, 13, 46]);
        else
            Should.Throw<BadRequestException>(() => RasterOptionRules.ParseBbox(text));
    }
}
