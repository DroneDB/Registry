#nullable enable
using System;
using System.IO;
using System.Linq;
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
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// The image/tile/raster-info pipeline must answer 404
/// "not processed yet" — not 500 — when the build artifact of a buildable entry
/// does not exist on disk. The guard (ObjectsManager.EnsureBuildArtifactAvailable)
/// runs BEFORE the response cache, and non-buildable entries must skip it on the
/// string-equality test without touching the file system.
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
    private Mock<IOptions<AppSettings>> _appSettingsMock = null!;
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
        _appSettingsMock = new Mock<IOptions<AppSettings>>();

        var settings = new AppSettings { DatasetsPath = "/tmp/datasets", DefaultThumbnailSize = 128 };
        _appSettingsMock.Setup(o => o.Value).Returns(settings);
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

        _utilsMock.Setup(x => x.GetDataset(OrgSlug, DsSlug, It.IsAny<bool>(), It.IsAny<bool>())).Returns(_ds);
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

    private ObjectsManager CreateManager()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var ctx = new RegistryContext(options);
        var buildPending = new BuildPendingService(
            ctx, _ddbManagerMock.Object, _backgroundJobMock.Object,
            _cacheManagerMock.Object, Mock.Of<ILogger<BuildPendingService>>());
        return new ObjectsManager(
            _logger,
            ctx,
            _appSettingsMock.Object,
            _ddbManagerMock.Object,
            _utilsMock.Object,
            _authManagerMock.Object,
            _cacheManagerMock.Object,
            _fsMock.Object,
            _backgroundJobMock.Object,
            _ddbWrapperMock.Object,
            _thumbsMock.Object,
            _jobIndexQueryMock.Object,
            buildPending);
    }

    private void SetupEntry(string path, EntryType type, string hash)
    {
        var entry = new Entry { Path = path, Type = type, Hash = hash, Size = 100 };
        _ddbMock.Setup(x => x.Search(path, It.IsAny<bool>())).Returns(new[] { entry });
        _ddbMock.Setup(x => x.GetEntry(path)).Returns(entry);
    }

    private static bool IsCopcArtifact(string localPath) =>
        localPath.Replace('\\', '/').EndsWith(".ddb/build/abc123/copc/cloud.copc.laz");

    private static bool IsCogArtifact(string localPath) =>
        localPath.Replace('\\', '/').EndsWith(".ddb/build/raster1/cog/cog.tif");

    [Test]
    public async Task GenerateThumbnailData_PointCloud_MissingCopcArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();

        var ex = await Should.ThrowAsync<NotFoundException>(
            () => mgr.GenerateThumbnailData(OrgSlug, DsSlug, "data/cloud.laz", null));

        ex.Message.ShouldContain("not available yet");
        // Pre-cache guard: nothing was generated or cached. The COPC path is probed
        // twice: once by GetBuildSource (legacy EPT fallback detection) and once by
        // the availability guard -> exactly 2 Exists calls.
        _fsMock.Verify(x => x.Exists(It.Is<string>(p => IsCopcArtifact(p))), Times.Exactly(2));
        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailData_PointCloud_ArtifactPresent_GeneratesThumbnail()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        _fsMock.Setup(x => x.Exists(It.Is<string>(p => IsCopcArtifact(p)))).Returns(true);

        var mgr = CreateManager();
        var result = await mgr.GenerateThumbnailData(OrgSlug, DsSlug, "data/cloud.laz", null);

        result.Data.ShouldBe(ThumbBytes);
        result.ContentType.ShouldBe("image/webp");
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()), Times.Once);
    }

    [Test]
    public async Task GenerateThumbnailData_NonBuildable_SkipsArtifactProbe()
    {
        SetupEntry("photo.jpg", EntryType.GeoImage, "img1");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();
        var result = await mgr.GenerateThumbnailData(OrgSlug, DsSlug, "photo.jpg", null);

        // GeoImage is non-buildable: sourcePath == entry.Path -> zero _fs.Exists probes,
        // thumbnail generated from the source file itself.
        result.Data.ShouldBe(ThumbBytes);
        _fsMock.Verify(x => x.Exists(It.IsAny<string>()), Times.Never);
        _thumbsMock.Verify(t => t.GenerateThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Stream>()), Times.Once);
    }

    [Test]
    public async Task GenerateTileData_GeoRaster_MissingCogArtifact_ThrowsNotFound_BeforeCache()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.GenerateTileData(OrgSlug, DsSlug, "ortho.tif", 1, 0, 0, false));

        _fsMock.Verify(x => x.Exists(It.Is<string>(p => IsCogArtifact(p))), Times.Once);
        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
        _ddbMock.Verify(x => x.GenerateTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GenerateThumbnailDataEx_PointCloud_MissingArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.GenerateThumbnailDataEx(OrgSlug, DsSlug, "data/cloud.laz", null));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Test]
    public async Task GenerateTileDataEx_PointCloud_MissingArtifact_ThrowsNotFound()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.GenerateTileDataEx(OrgSlug, DsSlug, "data/cloud.laz", 1, 0, 0, false));

        _cacheManagerMock.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Test]
    public async Task GetRasterInfo_GeoRaster_MissingCogArtifact_ThrowsNotFound()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(() => mgr.GetRasterInfo(OrgSlug, DsSlug, "ortho.tif"));

        _ddbMock.Verify(x => x.GetRasterInfo(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetRasterInfo_PointCloud_LegacyEptFallback_UsesEptArtifact()
    {
        SetupEntry("data/cloud.laz", EntryType.PointCloud, "abc123");
        _ddbMock.Setup(x => x.GetRasterInfo(It.IsAny<string>())).Returns("{}");
        // COPC missing -> GetBuildSource falls back to EPT; the guard must then probe
        // (and accept) the EPT artifact, not 404 a healthy legacy build.
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);
        _fsMock.Setup(x => x.Exists(It.Is<string>(p =>
                p.Replace('\\', '/').EndsWith(".ddb/build/abc123/ept/ept.json"))))
            .Returns(true);

        var mgr = CreateManager();
        var json = await mgr.GetRasterInfo(OrgSlug, DsSlug, "data/cloud.laz");

        json.ShouldBe("{}");
        _ddbMock.Verify(x => x.GetRasterInfo(It.Is<string>(p =>
            p.Replace('\\', '/').Contains("ept/ept.json"))), Times.Once);
    }

    [Test]
    public async Task ExportRaster_GeoRaster_MissingCogArtifact_ThrowsNotFound()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.ExportRaster(OrgSlug, DsSlug, "ortho.tif"));

        _ddbMock.Verify(x => x.ExportRaster(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task EstimateExportSize_GeoRaster_MissingCogArtifact_ThrowsNotFound()
    {
        SetupEntry("ortho.tif", EntryType.GeoRaster, "raster1");
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            () => mgr.EstimateExportSize(OrgSlug, DsSlug, "ortho.tif"));

        _ddbMock.Verify(x => x.GetRasterInfo(It.IsAny<string>()), Times.Never);
    }
}
