#nullable enable
using System;
using System.Threading;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
using Registry.Web.Test.Support;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// Focused unit tests for the entry transfer service: copy (intra-dataset) and transfer
/// (cross-dataset) covering:
///   - storage quota delta on overwrite copy
///   - atomic overwrite ordering (move-to-trash before DDB index removal)
///   - keepSource semantics in Transfer
///   - build folder copy semantics (always copy, never move)
///   - the pending-build retry an entry that arrives starts (copy, transfer, move)
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

    private Mock<IDatasetResolver> _utilsMock = null!;
    private Mock<IStorageQuota> _quotaMock = null!;
    private Mock<IAuthManager> _authManagerMock = null!;
    private Mock<IDdbManager> _ddbManagerMock = null!;
    private Mock<ICacheManager> _cacheManagerMock = null!;
    private Mock<IFileSystem> _fsMock = null!;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock = null!;
    private Mock<IDdbWrapper> _ddbWrapperMock = null!;
    private Mock<IThumbnailGenerator> _thumbsMock = null!;
    private Mock<IJobIndexQuery> _jobIndexQueryMock = null!;
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
        _utilsMock = new Mock<IDatasetResolver>();
        _quotaMock = new Mock<IStorageQuota>();
        _authManagerMock = new Mock<IAuthManager>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _cacheManagerMock = new Mock<ICacheManager>();
        _fsMock = new Mock<IFileSystem>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _ddbWrapperMock = new Mock<IDdbWrapper>();
        _thumbsMock = new Mock<IThumbnailGenerator>();
        _jobIndexQueryMock = new Mock<IJobIndexQuery>();
        _settings = new AppSettings { DatasetsPath = "/tmp/datasets" };
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

        _utilsMock.Setup(x => x.GetDatasetAsync(OrgSlug, DsSlug, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_ds);
        _utilsMock.Setup(x => x.GetDatasetAsync(OrgSlug, OtherDsSlug, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_otherDs);

        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        _ddbMock = new Mock<IDDB>();
        _ddbMock.SetupGet(x => x.DatasetFolderPath).Returns(DatasetFolder);
        _ddbMock.SetupGet(x => x.DerivedFolderPaths).Returns([Path.Combine(DatasetFolder, ".ddb", "build")]);
        _ddbMock.Setup(x => x.GetLocalPath(It.IsAny<string>()))
            .Returns<string>(p => Path.Combine(DatasetFolder, p));
        _ddbMock.SetupBuilds();

        _otherDdbMock = new Mock<IDDB>();
        _otherDdbMock.SetupGet(x => x.DatasetFolderPath).Returns(OtherDatasetFolder);
        _otherDdbMock.SetupGet(x => x.DerivedFolderPaths).Returns([Path.Combine(OtherDatasetFolder, ".ddb", "build")]);
        _otherDdbMock.Setup(x => x.GetLocalPath(It.IsAny<string>()))
            .Returns<string>(p => Path.Combine(OtherDatasetFolder, p));
        _otherDdbMock.SetupBuilds();

        _ddbManagerMock.Setup(x => x.Get(OrgSlug, _ds.InternalRef)).Returns(_ddbMock.Object);
        _ddbManagerMock.Setup(x => x.Get(OrgSlug, _otherDs.InternalRef)).Returns(_otherDdbMock.Object);
    }

    // The build-pending service is the builder default: a real one on the SUT context and these mocks.
    private IEntryTransferService CreateManager() => CreateServices().Transfers;

    private ObjectServices CreateServices() =>
        new ObjectServicesBuilder(TestDb.CreateContext<RegistryContext>())
            .WithLogger(_logger)
            .WithSettings(TestSettings.Options(_settings))
            .WithDdbManager(_ddbManagerMock.Object)
            .WithDatasets(_utilsMock.Object)
            .WithQuota(_quotaMock.Object)
            .WithAuthManager(_authManagerMock.Object)
            .WithCacheManager(_cacheManagerMock.Object)
            .WithFileSystem(_fsMock.Object)
            .WithBackgroundJobs(_backgroundJobMock.Object)
            .WithDdbWrapper(_ddbWrapperMock.Object)
            .WithThumbnailGenerator(_thumbsMock.Object)
            .WithJobIndexQuery(_jobIndexQueryMock.Object)
            .Build();

    // ---------------------------------------------------------------------
    // Copy
    // ---------------------------------------------------------------------

    [Test]
    public async Task Copy_SameSourceAndDest_ThrowsBadRequest()
    {
        var mgr = CreateManager();
        await Should.ThrowAsync<BadRequestException>(
            () => mgr.CopyAsync(OrgSlug, DsSlug, "a.jpg", "a.jpg"));
    }

    [Test]
    public async Task Copy_DestExists_NoOverwrite_Throws()
    {
        _ddbMock.Setup(x => x.GetEntry("src.jpg"))
            .Returns(new Entry { Path = "src.jpg", Type = EntryType.Generic, Size = 10, Hash = "h1" });
        _ddbMock.Setup(x => x.GetEntry("dst.jpg"))
            .Returns(new Entry { Path = "dst.jpg", Type = EntryType.Generic, Size = 5, Hash = "h2" });

        var mgr = CreateManager();
        var ex = await Should.ThrowAsync<ConflictException>(
            () => mgr.CopyAsync(OrgSlug, DsSlug, "src.jpg", "dst.jpg"));
        ex.Message.ShouldContain("overwrite");
    }

    [Test]
    public async Task Copy_CannotCopyIntoDescendant()
    {
        var mgr = CreateManager();
        var ex = await Should.ThrowAsync<BadRequestException>(
            () => mgr.CopyAsync(OrgSlug, DsSlug, "folder", "folder/inside"));
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
        _quotaMock.Setup(x => x.CheckCurrentUserStorage(It.IsAny<long>()))
            .Callback<long>(d => observedDelta = d)
            .ThrowsAsync(new TestStopException("stop"));

        var mgr = CreateManager();
        await Should.ThrowAsync<TestStopException>(
            () => mgr.CopyAsync(OrgSlug, DsSlug, "src.jpg", "dst.jpg", overwrite: true));

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
        _quotaMock.Setup(x => x.CheckCurrentUserStorage(It.IsAny<long>()))
            .Callback<long>(d => observedDelta = d)
            .ThrowsAsync(new TestStopException("stop"));

        var mgr = CreateManager();
        await Should.ThrowAsync<TestStopException>(
            () => mgr.CopyAsync(OrgSlug, DsSlug, "src.jpg", "dst.jpg", overwrite: true));

        observedDelta.ShouldBe(0);
    }

    [Test]
    public async Task Copy_NoOverwrite_ChargesFullSourceSize()
    {
        _ddbMock.Setup(x => x.GetEntry("src.jpg"))
            .Returns(new Entry { Path = "src.jpg", Type = EntryType.Generic, Size = 100, Hash = "hs" });
        _ddbMock.Setup(x => x.GetEntry("dst.jpg")).Returns((Entry?)null);

        long? observedDelta = null;
        _quotaMock.Setup(x => x.CheckCurrentUserStorage(It.IsAny<long>()))
            .Callback<long>(d => observedDelta = d)
            .ThrowsAsync(new TestStopException("stop"));

        var mgr = CreateManager();
        await Should.ThrowAsync<TestStopException>(
            () => mgr.CopyAsync(OrgSlug, DsSlug, "src.jpg", "dst.jpg"));

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
            await mgr.CopyAsync(OrgSlug, DsSlug, "src.jpg", "dst.jpg", overwrite: true);
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
        await mgr.TransferAsync(OrgSlug, DsSlug, sourcePath, OrgSlug, OtherDsSlug,
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
        await mgr.TransferAsync(OrgSlug, DsSlug, sourcePath, OrgSlug, OtherDsSlug,
            destPath: destPath, overwrite: false, keepSource: false);

        _ddbMock.Verify(x => x.Remove(sourcePath), Times.Once);
        _otherDdbMock.Verify(x => x.AddRaw(destPath), Times.Once);
    }

    [Test]
    public async Task Transfer_CopiesTheBuildThroughLibddb_NeverByHand()
    {
        const string sourcePath = "cloud.laz";
        const string destPath = "cloud.laz";
        const string hash = "deadbeef";

        _ddbMock.Setup(x => x.GetEntry(sourcePath))
            .Returns(new Entry { Path = sourcePath, Type = EntryType.PointCloud, Size = 10, Hash = hash });
        _ddbMock.SetupBuilds(TestBuilds.Build(DatasetFolder, sourcePath, EntryType.PointCloud, hash,
            BuildState.Complete, TestBuilds.File(ArtifactRoles.PointCloudCopc, "copc/cloud.copc.laz")));
        _otherDdbMock.Setup(x => x.GetEntry(destPath)).Returns((Entry?)null);
        _otherDdbMock.Setup(x => x.EntryExists(destPath)).Returns(true);

        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(true);
        _fsMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);

        var mgr = CreateManager();
        await mgr.TransferAsync(OrgSlug, DsSlug, sourcePath, OrgSlug, OtherDsSlug,
            destPath: destPath, overwrite: false, keepSource: false);

        // DDBCopyBuildArtifacts: the artifacts and the pending marker, copied (a build may serve other
        // entries with the same content), never a build folder named by Registry
        _ddbMock.Verify(x => x.CopyBuildArtifactsTo(OtherDatasetFolder, hash), Times.Once);
        _fsMock.Verify(x => x.FolderCopy(It.Is<string>(s => s.Contains(hash)), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        _fsMock.Verify(x => x.FolderMove(It.Is<string>(s => s.Contains(hash)), It.IsAny<string>()), Times.Never);
    }

    // ---------------------------------------------------------------------
    // The pending-build retry of an entry that arrives
    // ---------------------------------------------------------------------

    // The datasets of the pending-build coordinators queued (DdbJobs.BuildPending)
    private List<string> RecordCoordinators()
    {
        var coordinators = new List<string>();
        _backgroundJobMock.Setup(j => j.EnqueueIndexed(
                It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(),
                It.IsAny<Registry.Web.Models.IndexPayload>()))
            .Returns<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>,
                Registry.Web.Models.IndexPayload>((job, meta) =>
            {
                if (((System.Linq.Expressions.MethodCallExpression)job.Body).Method.Name ==
                    nameof(Registry.Web.Services.Jobs.DdbJobs.BuildPending))
                    coordinators.Add(meta.DsSlug);
                return "job-" + Guid.NewGuid().ToString("N");
            });
        return coordinators;
    }

    /// <summary>
    /// The transferred file may be what a pending build of the destination waited for (libddb makes that build due
    /// when the file is indexed): the destination's coordinator starts (red before: only the hourly sweep found it).
    /// </summary>
    [Test]
    public async Task Transfer_IntoADatasetWithRetryableWork_StartsTheDestinationsCoordinator()
    {
        _ddbMock.Setup(x => x.GetEntry("model.mtl"))
            .Returns(new Entry { Path = "model.mtl", Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _otherDdbMock.Setup(x => x.GetEntry("model.mtl")).Returns((Entry?)null);
        _otherDdbMock.Setup(x => x.EntryExists("model.mtl")).Returns(true);
        _otherDdbMock.Setup(x => x.IsBuildPending()).Returns(true);
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(true);
        var coordinators = RecordCoordinators();

        await CreateManager().TransferAsync(OrgSlug, DsSlug, "model.mtl", OrgSlug, OtherDsSlug, keepSource: true);

        coordinators.ShouldBe([OtherDsSlug]);
    }

    [Test]
    public async Task Copy_IntoADatasetWithRetryableWork_StartsItsCoordinator()
    {
        _ddbMock.Setup(x => x.GetEntry("old/model.mtl"))
            .Returns(new Entry { Path = "old/model.mtl", Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _ddbMock.SetupSequence(x => x.GetEntry("model.mtl"))
            .Returns((Entry?)null)
            .Returns(new Entry { Path = "model.mtl", Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _ddbMock.Setup(x => x.IsBuildPending()).Returns(true);
        _fsMock.Setup(x => x.Exists(It.IsAny<string>())).Returns(true);
        var coordinators = RecordCoordinators();

        await CreateManager().CopyAsync(OrgSlug, DsSlug, "old/model.mtl", "model.mtl");

        coordinators.ShouldBe([DsSlug]);
    }

    /// <summary>A companion renamed to the name its main file expects unblocks the build that waited for it.</summary>
    [Test]
    public async Task Move_InADatasetWithRetryableWork_StartsItsCoordinator()
    {
        _ddbMock.Setup(x => x.GetEntry("model.mtl.txt"))
            .Returns(new Entry { Path = "model.mtl.txt", Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _ddbMock.SetupSequence(x => x.GetEntry("model.mtl"))
            .Returns((Entry?)null)
            .Returns(new Entry { Path = "model.mtl", Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _ddbMock.Setup(x => x.IsBuildPending()).Returns(true);
        var coordinators = RecordCoordinators();

        await CreateServices().Commands.MoveAsync(OrgSlug, DsSlug, "model.mtl.txt", "model.mtl");

        coordinators.ShouldBe([DsSlug]);
    }

    [Test]
    public async Task Move_InADatasetWithNothingToRetry_StartsNoCoordinator()
    {
        _ddbMock.Setup(x => x.GetEntry("a.txt"))
            .Returns(new Entry { Path = "a.txt", Type = EntryType.Generic, Size = 10, Hash = "hh" });
        _ddbMock.SetupSequence(x => x.GetEntry("b.txt"))
            .Returns((Entry?)null)
            .Returns(new Entry { Path = "b.txt", Type = EntryType.Generic, Size = 10, Hash = "hh" });
        var coordinators = RecordCoordinators();

        await CreateServices().Commands.MoveAsync(OrgSlug, DsSlug, "a.txt", "b.txt");

        coordinators.ShouldBeEmpty();
        _ddbMock.Verify(x => x.IsBuildPending(), Times.Once);
    }

    /// <summary>The pending builds listing says which builds wait for files, so a client can tell them apart.</summary>
    [Test]
    public async Task PendingBuilds_TellWhichBuildsWaitForFiles()
    {
        var waiting = Registry.Adapters.Testing.PendingMarkers.Of("model.obj", "h1", 0, "model.mtl");
        waiting.AwaitingFiles = true;
        _ddbMock.Setup(x => x.GetPendingBuildInfo())
            .Returns([waiting, Registry.Adapters.Testing.PendingMarkers.Of("cloud.laz", "h2")]);

        var pending = (await CreateServices().Builds.GetPendingBuildsAsync(OrgSlug, DsSlug)).ToArray();

        pending.Select(p => (p.Path, p.AwaitingFiles)).ShouldBe([("model.obj", true), ("cloud.laz", false)]);
    }
}
