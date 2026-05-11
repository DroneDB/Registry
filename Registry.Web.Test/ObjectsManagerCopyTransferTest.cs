#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// Focused unit tests for ObjectsManager.Copy (intra-dataset) and Transfer
/// (cross-dataset) covering:
///   - storage quota delta on overwrite copy
///   - atomic overwrite ordering (move-to-trash before DDB index removal)
///   - keepSource semantics in Transfer
///   - build folder copy semantics (always copy, never move)
/// </summary>
[TestFixture]
public class ObjectsManagerCopyTransferTest : TestBase
{
    // Marker exception used to short-circuit Copy/Transfer flows after the
    // assertion-relevant side-effects have been observed, so we don't need
    // to mock the entire downstream pipeline.
    private sealed class TestStopException : Exception
    {
        public TestStopException(string message) : base(message) { }
    }

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
    private AppSettings _settings = null!;
    private ILogger<ObjectsManager> _logger = null!;

    private Dataset _ds = null!;
    private Dataset _otherDs = null!;
    private Mock<IDDB> _ddbMock = null!;
    private Mock<IDDB> _otherDdbMock = null!;

    private const string OrgSlug = "test-org";
    private const string DsSlug = "test-dataset";
    private const string OtherDsSlug = "test-dataset-2";
    private const string DatasetFolder = "/tmp/datasets/test-org/test-dataset";
    private const string OtherDatasetFolder = "/tmp/datasets/test-org/test-dataset-2";

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
        _settings = new AppSettings { DatasetsPath = "/tmp/datasets" };
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);
        _logger = CreateTestLogger<ObjectsManager>();

        var org = new Organization { Slug = OrgSlug, Name = "Org", CreationDate = DateTime.UtcNow, OwnerId = "u" };
        _ds = new Dataset
        {
            Slug = DsSlug,
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.UtcNow,
            Organization = org
        };
        _otherDs = new Dataset
        {
            Slug = OtherDsSlug,
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.UtcNow,
            Organization = org
        };

        _utilsMock.Setup(x => x.GetDataset(OrgSlug, DsSlug, It.IsAny<bool>(), It.IsAny<bool>())).Returns(_ds);
        _utilsMock.Setup(x => x.GetDataset(OrgSlug, OtherDsSlug, It.IsAny<bool>(), It.IsAny<bool>())).Returns(_otherDs);

        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        _ddbMock = new Mock<IDDB>();
        _ddbMock.SetupGet(x => x.DatasetFolderPath).Returns(DatasetFolder);
        _ddbMock.SetupGet(x => x.BuildFolderPath).Returns(Path.Combine(DatasetFolder, ".ddb", "build"));
        _ddbMock.Setup(x => x.GetLocalPath(It.IsAny<string>()))
            .Returns<string>(p => Path.Combine(DatasetFolder, p));
        _ddbMock.Setup(x => x.IsBuildActive(It.IsAny<string>())).Returns(false);

        _otherDdbMock = new Mock<IDDB>();
        _otherDdbMock.SetupGet(x => x.DatasetFolderPath).Returns(OtherDatasetFolder);
        _otherDdbMock.SetupGet(x => x.BuildFolderPath).Returns(Path.Combine(OtherDatasetFolder, ".ddb", "build"));
        _otherDdbMock.Setup(x => x.GetLocalPath(It.IsAny<string>()))
            .Returns<string>(p => Path.Combine(OtherDatasetFolder, p));
        _otherDdbMock.Setup(x => x.IsBuildActive(It.IsAny<string>())).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(OrgSlug, _ds.InternalRef)).Returns(_ddbMock.Object);
        _ddbManagerMock.Setup(x => x.Get(OrgSlug, _otherDs.InternalRef)).Returns(_otherDdbMock.Object);
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

    // ---------------------------------------------------------------------
    // Copy
    // ---------------------------------------------------------------------

    [Test]
    public async Task Copy_SameSourceAndDest_ThrowsInvalidOperation()
    {
        var mgr = CreateManager();
        await Should.ThrowAsync<InvalidOperationException>(
            () => mgr.Copy(OrgSlug, DsSlug, "a.jpg", "a.jpg"));
    }

    [Test]
    public async Task Copy_DestExists_NoOverwrite_Throws()
    {
        _ddbMock.Setup(x => x.GetEntry("src.jpg"))
            .Returns(new Entry { Path = "src.jpg", Type = EntryType.Generic, Size = 10, Hash = "h1" });
        _ddbMock.Setup(x => x.GetEntry("dst.jpg"))
            .Returns(new Entry { Path = "dst.jpg", Type = EntryType.Generic, Size = 5, Hash = "h2" });

        var mgr = CreateManager();
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mgr.Copy(OrgSlug, DsSlug, "src.jpg", "dst.jpg"));
        ex.Message.ShouldContain("overwrite");
    }

    [Test]
    public async Task Copy_CannotCopyIntoDescendant()
    {
        var mgr = CreateManager();
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mgr.Copy(OrgSlug, DsSlug, "folder", "folder/inside"));
        ex.Message.ShouldContain("descendants");
    }

    [Test]
    public async Task Copy_Overwrite_ChargesOnlyStorageDelta()
    {
        _ddbMock.Setup(x => x.GetEntry("src.jpg"))
            .Returns(new Entry { Path = "src.jpg", Type = EntryType.Generic, Size = 100, Hash = "hs" });
        _ddbMock.Setup(x => x.GetEntry("dst.jpg"))
            .Returns(new Entry { Path = "dst.jpg", Type = EntryType.Generic, Size = 30, Hash = "hd" });

        long? observedDelta = null;
        _utilsMock.Setup(x => x.CheckCurrentUserStorage(It.IsAny<long>()))
            .Callback<long>(d => observedDelta = d)
            .ThrowsAsync(new TestStopException("stop"));

        var mgr = CreateManager();
        await Should.ThrowAsync<TestStopException>(
            () => mgr.Copy(OrgSlug, DsSlug, "src.jpg", "dst.jpg", overwrite: true));

        observedDelta.ShouldBe(70);
    }

    [Test]
    public async Task Copy_Overwrite_DeltaClampedToZeroWhenDestLarger()
    {
        _ddbMock.Setup(x => x.GetEntry("src.jpg"))
            .Returns(new Entry { Path = "src.jpg", Type = EntryType.Generic, Size = 10, Hash = "hs" });
        _ddbMock.Setup(x => x.GetEntry("dst.jpg"))
            .Returns(new Entry { Path = "dst.jpg", Type = EntryType.Generic, Size = 50, Hash = "hd" });

        long? observedDelta = null;
        _utilsMock.Setup(x => x.CheckCurrentUserStorage(It.IsAny<long>()))
            .Callback<long>(d => observedDelta = d)
            .ThrowsAsync(new TestStopException("stop"));

        var mgr = CreateManager();
        await Should.ThrowAsync<TestStopException>(
            () => mgr.Copy(OrgSlug, DsSlug, "src.jpg", "dst.jpg", overwrite: true));

        observedDelta.ShouldBe(0);
    }

    [Test]
    public async Task Copy_NoOverwrite_ChargesFullSourceSize()
    {
        _ddbMock.Setup(x => x.GetEntry("src.jpg"))
            .Returns(new Entry { Path = "src.jpg", Type = EntryType.Generic, Size = 100, Hash = "hs" });
        _ddbMock.Setup(x => x.GetEntry("dst.jpg")).Returns((Entry?)null);

        long? observedDelta = null;
        _utilsMock.Setup(x => x.CheckCurrentUserStorage(It.IsAny<long>()))
            .Callback<long>(d => observedDelta = d)
            .ThrowsAsync(new TestStopException("stop"));

        var mgr = CreateManager();
        await Should.ThrowAsync<TestStopException>(
            () => mgr.Copy(OrgSlug, DsSlug, "src.jpg", "dst.jpg"));

        observedDelta.ShouldBe(100);
    }

    [Test]
    public async Task Copy_AtomicOverwrite_MovesDestToTrashBeforeRemovingFromIndex()
    {
        _ddbMock.Setup(x => x.GetEntry("src.jpg"))
            .Returns(new Entry { Path = "src.jpg", Type = EntryType.Generic, Size = 10, Hash = "hs" });
        _ddbMock.Setup(x => x.GetEntry("dst.jpg"))
            .Returns(new Entry { Path = "dst.jpg", Type = EntryType.Generic, Size = 10, Hash = "hd" });
        _ddbMock.Setup(x => x.EntryExists("dst.jpg")).Returns(true);

        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(true);
        _fsMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(false);

        var operations = new List<string>();
        _fsMock.Setup(x => x.Move(It.IsAny<string>(), It.Is<string>(s => s.Contains(".trash-"))))
            .Callback(() => operations.Add("fs-move-to-trash"));
        _ddbMock.Setup(x => x.Remove("dst.jpg"))
            .Callback(() => operations.Add("ddb-remove"));

        var mgr = CreateManager();
        try
        {
            await mgr.Copy(OrgSlug, DsSlug, "src.jpg", "dst.jpg", overwrite: true);
        }
        catch
        {
            // downstream may throw because we don't fully mock everything
        }

        operations.ShouldContain("fs-move-to-trash");
        operations.ShouldContain("ddb-remove");
        operations.IndexOf("fs-move-to-trash").ShouldBeLessThan(operations.IndexOf("ddb-remove"));
    }

    // ---------------------------------------------------------------------
    // Transfer
    // ---------------------------------------------------------------------

    [Test]
    public async Task Transfer_KeepSourceTrue_DoesNotRemoveSourceEntry()
    {
        const string sourcePath = "src.jpg";
        const string destPath = "src.jpg";

        _ddbMock.Setup(x => x.GetEntry(sourcePath))
            .Returns(new Entry { Path = sourcePath, Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _otherDdbMock.Setup(x => x.GetEntry(destPath)).Returns((Entry?)null);
        _otherDdbMock.Setup(x => x.EntryExists(destPath)).Returns(true);

        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(true);
        _fsMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();
        await mgr.Transfer(OrgSlug, DsSlug, sourcePath, OrgSlug, OtherDsSlug,
            destPath: destPath, overwrite: false, keepSource: true);

        _ddbMock.Verify(x => x.Remove(It.IsAny<string>()), Times.Never);
        _fsMock.Verify(x => x.Delete(It.IsAny<string>()), Times.Never);
        _fsMock.Verify(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _otherDdbMock.Verify(x => x.AddRaw(destPath), Times.Once);
    }

    [Test]
    public async Task Transfer_KeepSourceFalse_RemovesSourceEntry()
    {
        const string sourcePath = "src.jpg";
        const string destPath = "src.jpg";

        _ddbMock.Setup(x => x.GetEntry(sourcePath))
            .Returns(new Entry { Path = sourcePath, Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _otherDdbMock.Setup(x => x.GetEntry(destPath)).Returns((Entry?)null);
        _otherDdbMock.Setup(x => x.EntryExists(destPath)).Returns(true);

        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(true);
        _fsMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(false);

        var mgr = CreateManager();
        await mgr.Transfer(OrgSlug, DsSlug, sourcePath, OrgSlug, OtherDsSlug,
            destPath: destPath, overwrite: false, keepSource: false);

        _ddbMock.Verify(x => x.Remove(sourcePath), Times.Once);
        _otherDdbMock.Verify(x => x.AddRaw(destPath), Times.Once);
    }

    [Test]
    public async Task Transfer_BuildFolder_AlwaysCopiedNeverMoved()
    {
        const string sourcePath = "cloud.laz";
        const string destPath = "cloud.laz";
        const string hash = "deadbeef";

        _ddbMock.Setup(x => x.GetEntry(sourcePath))
            .Returns(new Entry { Path = sourcePath, Type = EntryType.PointCloud, Size = 10, Hash = hash });
        _otherDdbMock.Setup(x => x.GetEntry(destPath)).Returns((Entry?)null);
        _otherDdbMock.Setup(x => x.EntryExists(destPath)).Returns(true);

        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(true);
        _fsMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);

        var mgr = CreateManager();
        await mgr.Transfer(OrgSlug, DsSlug, sourcePath, OrgSlug, OtherDsSlug,
            destPath: destPath, overwrite: false, keepSource: false);

        _fsMock.Verify(
            x => x.FolderCopy(It.Is<string>(s => s.Contains(hash)), It.Is<string>(s => s.Contains(hash)),
                It.IsAny<bool>(), It.IsAny<bool>()),
            Times.AtLeastOnce);
        _fsMock.Verify(
            x => x.FolderMove(It.Is<string>(s => s.Contains(hash)), It.Is<string>(s => s.Contains(hash))),
            Times.Never);
    }
}
