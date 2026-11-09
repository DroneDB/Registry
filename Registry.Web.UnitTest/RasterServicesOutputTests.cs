using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.Storage;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Managers;
using Entry = Registry.Ports.DroneDB.Entry;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The output rules of the synchronous merge and of Mask Borders (FR-PRC-01, FR-XC-07, D-040): an output that is one
/// of the bands is a 400, an existing output without <c>overwrite</c> a 409 with its path and nothing is removed, a
/// replace goes to the existing entry (any letter case); libddb is asked to replace only a file that exists when the
/// request (merge) or the job (mask) decides it, so an output another writer creates meanwhile is refused (409) and
/// kept; the mask job checks again when it runs, records the written output (<see cref="PublishedOutputMarker"/>) and a
/// later attempt only indexes and builds it; a job stored before the <c>overwrite</c> parameter replaces nothing.
/// </summary>
[TestFixture]
public class RasterServicesOutputTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string Folder = "/datasets/org/ds";
    private const string Previous = "the file another user uploaded\n";

    private Mock<IDDB> _ddb = null!;
    private Mock<IFileSystem> _fs = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private List<Job> _enqueued = null!;
    private List<EntrySummary> _entries = null!;
    private ObjectServices _services = null!;
    private Mock<IStorageQuota> _quota = null!;
    private Guid _ref;
    private string _root = null!;
    private readonly List<InMemoryStorage> _storages = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var storage in _storages)
            storage.Dispose();
        _storages.Clear();
        Directory.Delete(_root, recursive: true);
    }

    [SetUp]
    public void SetUp()
    {
        _entries = [];
        _enqueued = [];
        // The files of the dataset: the replace is decided by the file that is there
        _root = Directory.CreateTempSubdirectory("raster-output-tests").FullName;

        var org = new Organization { Slug = Org, Name = "Org", CreationDate = DateTime.UtcNow, OwnerId = "u" };
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org };
        _ref = dataset.InternalRef;
        var datasets = new Mock<IDatasetResolver>();
        datasets.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        _ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(_root, p));
        _ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns<string>(p => _entries.Any(e => e.Path == p));
        _ddb.Setup(d => d.GetEntry(It.IsAny<string>())).Returns<string>(p =>
            _entries.FirstOrDefault(e => e.Path == p) is { } e
                ? new Entry { Path = e.Path, Type = e.Type, Hash = "h-" + e.Path, Size = 1 }
                : null);
        _ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns<EntryQuery>(q =>
            _entries.Where(e => Parent(e.Path) == (q.Path ?? string.Empty)).ToList());
        _ddb.Setup(d => d.ValidateMergeMultispectral(It.IsAny<string[]>())).Returns("{}");
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(_ddb.Object);

        _fs = new Mock<IFileSystem>();
        _fs.Setup(f => f.Exists(It.IsAny<string>())).Returns(true);

        _jobs = new Mock<IBackgroundJobsProcessor>();
        _jobs.Setup(j => j.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Action<DdbJobs>>, IndexPayload>((call, _) => _enqueued.Add(Job.FromExpression(call)))
            .Returns("job-1");

        // No limit unless a test sets one for the owner "u" of the organization
        _quota = new Mock<IStorageQuota>();

        _services = new ObjectServicesBuilder(TestDb.CreateContext<RegistryContext>())
            .WithQuota(_quota.Object)
            .WithDatasets(datasets.Object)
            .WithAuthManager(auth.Object)
            .WithDdbManager(ddbManager.Object)
            .WithFileSystem(_fs.Object)
            .WithBackgroundJobs(_jobs.Object)
            .WithJobIndexQuery(Mock.Of<IJobIndexQuery>())
            .Build();
    }

    private void Entry(string path, EntryType type = EntryType.GeoRaster) =>
        _entries.Add(new EntrySummary { Path = path, Type = type });

    // A file on disk at the dataset path, as another writer left it
    private string OnDisk(string path)
    {
        var local = Path.Combine(_root, path);
        File.WriteAllText(local, Previous);
        return local;
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    // ---- Merge (synchronous) ----

    [Test]
    public async Task Merge_OutputThatIsABand_Is400_AndRemovesNothing()
    {
        Entry("b1.tif");
        Entry("b2.tif");

        var ex = await Should.ThrowAsync<BadRequestException>(() =>
            _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "B1.tif", overwrite: true));

        ex.Message.ShouldBe("The output 'B1.tif' is also one of the input files: choose another output path.");
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
        _ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task Merge_ExistingOutputWithoutOverwrite_Is409_AndRemovesNothing()
    {
        Entry("b1.tif");
        Entry("b2.tif");
        Entry("Merged.tif");

        var ex = await Should.ThrowAsync<NameClashException>(() =>
            _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "merged.tif"));

        ex.Message.ShouldBe("A file or folder named 'Merged.tif' already exists in this folder");
        ex.ConflictPath.ShouldBe("Merged.tif");
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
        _ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task Merge_WithOverwrite_ReplacesTheExistingEntry()
    {
        Entry("b1.tif");
        Entry("b2.tif");
        Entry("Merged.tif");
        OnDisk("Merged.tif");

        await _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "merged.tif", overwrite: true);

        _ddb.Verify(d => d.MergeMultispectral(new[] { "b1.tif", "b2.tif" }, "Merged.tif", true), Times.Once);
        _ddb.Verify(d => d.AddRaw("Merged.tif"), Times.Once);
        // libddb replaces it once the merge is complete (FR-PRC-01): nothing deletes it before
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Merge_WithOverwrite_FailedMerge_LeavesTheExistingFile()
    {
        Entry("b1.tif");
        Entry("b2.tif");
        Entry("Merged.tif");
        _ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new DdbException("merge failed"));

        await Should.ThrowAsync<DdbException>(() =>
            _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "merged.tif", overwrite: true));

        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
        _ddb.Verify(d => d.AddRaw(It.IsAny<string>()), Times.Never);
    }

    // FR-XC-26, SEC-003: the synchronous merge writes a file into the dataset: an owner of the organization with nothing
    // left is refused with 507 (whoever merges) before anything is removed or written
    [Test]
    public async Task Merge_ForAnOwnerWithNothingLeft_Is507_AndWritesNothing()
    {
        Entry("b1.tif");
        Entry("b2.tif");
        Entry("Merged.tif");
        _quota.Setup(q => q.RemainingBytesAsync("u", It.IsAny<CancellationToken>())).ReturnsAsync(0L);

        var ex = await Should.ThrowAsync<QuotaExceededException>(() =>
            _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "merged.tif", overwrite: true));

        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 0 B left.");
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
        _ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task Merge_WithOverwrite_NoOutputYet_MergesWithoutTheReplace()
    {
        Entry("b1.tif");
        Entry("b2.tif");

        await _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "merged.tif", overwrite: true);

        // Nothing to replace: libddb refuses a file that another writer creates meanwhile
        _ddb.Verify(d => d.MergeMultispectral(new[] { "b1.tif", "b2.tif" }, "merged.tif", false), Times.Once);
        _ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), true), Times.Never);
        _ddb.Verify(d => d.AddRaw("merged.tif"), Times.Once);
    }

    [Test]
    public async Task Merge_WithOverwrite_OutputCreatedByAnotherWriterMeanwhile_Is409_AndKeepsIt()
    {
        Entry("b1.tif");
        Entry("b2.tif");
        // Another user uploads merged.tif after the request, before the merge writes it: libddb (without the replace)
        // refuses the output that now exists
        _ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), "merged.tif", false))
            .Callback(() => OnDisk("merged.tif"))
            .Throws(new DdbException("Output file already exists"));

        var ex = await Should.ThrowAsync<NameClashException>(() =>
            _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "merged.tif", overwrite: true));

        ex.ConflictPath.ShouldBe("merged.tif");
        ex.Message.ShouldBe("A file or folder named 'merged.tif' already exists in this folder");
        File.ReadAllText(Path.Combine(_root, "merged.tif")).ShouldBe(Previous);
        _ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), true), Times.Never);
        _ddb.Verify(d => d.AddRaw(It.IsAny<string>()), Times.Never);
    }

    // ---- Mask borders (request) ----

    // FR-XC-26, SEC-003: the legacy Mask Borders endpoint writes a file: an owner of the organization with nothing left is
    // refused with 507 before the job is queued and before the copy it would replace is removed
    [Test]
    public async Task Mask_ForAnOwnerWithNothingLeft_Is507_QueuesNothing_AndRemovesNothing()
    {
        Entry("ortho.tif");
        Entry("ortho_masked.tif");
        _quota.Setup(q => q.RemainingBytesAsync("u", It.IsAny<CancellationToken>())).ReturnsAsync(0L);

        var ex = await Should.ThrowAsync<QuotaExceededException>(() =>
            _services.Processing.MaskBordersAsync(Org, Ds, "ortho.tif", 15, false, overwrite: true));

        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 0 B left.");
        _enqueued.ShouldBeEmpty();
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Mask_ForAnOwnerWithRoomLeft_IsQueued()
    {
        Entry("ortho.tif");
        _quota.Setup(q => q.RemainingBytesAsync("u", It.IsAny<CancellationToken>())).ReturnsAsync(1L);

        await _services.Processing.MaskBordersAsync(Org, Ds, "ortho.tif", 15, false);

        _enqueued.ShouldHaveSingleItem();
    }

    [Test]
    public async Task Mask_ExistingCopyWithoutOverwrite_Is409_AndQueuesNothing()
    {
        Entry("ortho.tif");
        Entry("ortho_masked.tif");

        var ex = await Should.ThrowAsync<NameClashException>(() =>
            _services.Processing.MaskBordersAsync(Org, Ds, "ortho.tif", 15, false));

        ex.Message.ShouldBe("A file or folder named 'ortho_masked.tif' already exists in this folder");
        ex.ConflictPath.ShouldBe("ortho_masked.tif");
        _enqueued.ShouldBeEmpty();
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Mask_WithOverwrite_QueuesTheReplaceOfTheExistingEntry()
    {
        Entry("ortho.tif");
        Entry("ORTHO_masked.TIF");

        await _services.Processing.MaskBordersAsync(Org, Ds, "ortho.tif", 15, false, overwrite: true);

        var job = _enqueued.ShouldHaveSingleItem();
        job.Method.GetParameters().Length.ShouldBe(8);
        job.Args.Take(7).ShouldBe(new object[] { Org, _ref, "ortho.tif", "ORTHO_masked.TIF", 15, false, true });
        // The job replaces it once the mask is complete (FR-PRC-01): the request deletes nothing
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Mask_NewCopy_QueuesAJobThatRefusesAnExistingOutput()
    {
        Entry("ortho.tif");

        await _services.Processing.MaskBordersAsync(Org, Ds, "ortho.tif", 15, false);

        _enqueued.ShouldHaveSingleItem().Args.Take(7)
            .ShouldBe(new object[] { Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, false });
        _fs.Verify(f => f.Delete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Mask_WithOverwrite_NoCopyYet_QueuesAJobThatRefusesAnExistingOutput()
    {
        Entry("ortho.tif");

        await _services.Processing.MaskBordersAsync(Org, Ds, "ortho.tif", 15, false, overwrite: true);

        // Nothing to replace at the request: a copy that appears before the job runs is not the user's to replace
        _enqueued.ShouldHaveSingleItem().Args.Take(7)
            .ShouldBe(new object[] { Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, false });
    }

    [Test]
    public async Task Mask_WithOverwrite_CopyCreatedBetweenRequestAndJob_IsRefused_AndKept()
    {
        Entry("ortho.tif");
        await _services.Processing.MaskBordersAsync(Org, Ds, "ortho.tif", 15, false, overwrite: true);
        var queued = _enqueued.ShouldHaveSingleItem();

        // Another user uploads ortho_masked.tif while the job waits in the queue
        Entry("ortho_masked.tif");
        OnDisk("ortho_masked.tif");
        var (jobs, _, context) = MaskJob();

        Should.Throw<NameClashException>(() => jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false,
                (bool)queued.Args[6], context))
            .ConflictPath.ShouldBe("ortho_masked.tif");

        File.ReadAllText(Path.Combine(_root, "ortho_masked.tif")).ShouldBe(Previous);
        _ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
        _ddb.Verify(d => d.AddRaw(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task MaskCheck_FindsTheCopyInAnyLetterCase()
    {
        Entry("ortho.tif");
        Entry("Ortho_Masked.tif");

        var check = await _services.Processing.CheckMaskedFileExistsAsync(Org, Ds, "ortho.tif");

        check.Exists.ShouldBeTrue();
        check.OutputPath.ShouldBe("Ortho_Masked.tif");
    }

    // ---- Mask borders (job) ----

    private (DdbJobs Jobs, Mock<IStorageConnection> Connection, PerformContext Context) MaskJob(string? published = null)
    {
        var manager = new Mock<IDdbManager>();
        manager.Setup(m => m.Open(Org, _ref)).Returns(_ddb.Object);
        var storage = new InMemoryStorage();
        _storages.Add(storage);
        var pending = new PendingBuildEnqueuer(TestJobs.Scheduler(Mock.Of<IBackgroundJobsProcessor>()),
            Mock.Of<IJobIndexQuery>(), manager.Object, storage, new FakeTime(DateTime.UtcNow));
        var jobs = new DdbJobs(manager.Object, pending, new FileSystem(), new CapturingLogger<DdbJobs>(),
            TestJobs.Budget(null));

        var connection = new Mock<IStorageConnection>();
        connection.Setup(c => c.GetJobParameter("7", PublishedOutputMarker.Key)).Returns(published!);
        var job = new BackgroundJob("7", Job.FromExpression<DdbJobs>(j => j.Cleanup(Org, _ref, null)), DateTime.UtcNow);
        var context = new PerformContext(null, connection.Object, job, Mock.Of<IJobCancellationToken>());
        return (jobs, connection, context);
    }

    [Test]
    public void MaskJob_OutputThatAppearedSinceTheRequest_IsRefused_WithoutOverwrite()
    {
        Entry("ortho_masked.tif");
        var (jobs, _, context) = MaskJob();

        Should.Throw<NameClashException>(() =>
                jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, false, context))
            .ConflictPath.ShouldBe("ortho_masked.tif");

        _ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public void MaskJob_WithOverwrite_LetsLibddbReplaceTheExistingOutput()
    {
        Entry("ortho_masked.tif");
        OnDisk("ortho_masked.tif");
        var (jobs, _, context) = MaskJob();

        jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, true, context);

        _ddb.Verify(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false, true), Times.Once);
    }

    [Test]
    public void MaskJob_WithOverwrite_NoOutputWhenItRuns_MasksWithoutTheReplace()
    {
        var (jobs, _, context) = MaskJob();

        jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, true, context);

        // The copy the request found is gone: libddb refuses a file that another writer creates while the mask runs
        _ddb.Verify(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false, false), Times.Once);
        _ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
            true), Times.Never);
    }

    [Test]
    public void MaskJob_OutputCreatedByAnotherWriterWhileItRuns_IsRefused_AndKept()
    {
        var (jobs, connection, context) = MaskJob();
        _ddb.Setup(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false, false))
            .Callback(() => OnDisk("ortho_masked.tif"))
            .Throws(new DdbException("Output file already exists"));

        Should.Throw<NameClashException>(() =>
                jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, true, context))
            .ConflictPath.ShouldBe("ortho_masked.tif");

        File.ReadAllText(Path.Combine(_root, "ortho_masked.tif")).ShouldBe(Previous);
        connection.Verify(c => c.SetJobParameter(It.IsAny<string>(), PublishedOutputMarker.Key, It.IsAny<string>()),
            Times.Never);
        _ddb.Verify(d => d.AddRaw(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void MaskJob_RecordsTheWrittenOutput_BeforeIndexingIt()
    {
        var order = new List<string>();
        var (jobs, connection, context) = MaskJob();
        _ddb.Setup(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false, false)).Callback(() => order.Add("mask"));
        connection.Setup(c => c.SetJobParameter("7", PublishedOutputMarker.Key, "ortho_masked.tif"))
            .Callback(() => order.Add("published"));
        _ddb.Setup(d => d.AddRaw("ortho_masked.tif")).Callback(() => order.Add("index"));
        _ddb.Setup(d => d.Build("ortho_masked.tif", null, false)).Callback(() => order.Add("build"));

        jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, false, context);

        order.ShouldBe(["mask", "published", "index", "build"]);
    }

    [Test]
    public void MaskJob_LaterAttempt_OnlyIndexesAndBuildsWhatTheFirstWrote()
    {
        var written = Path.GetTempFileName();
        try
        {
            Entry("ortho_masked.tif");
            _ddb.Setup(d => d.GetLocalPath("ortho_masked.tif")).Returns(written);
            var (jobs, _, context) = MaskJob(published: "ortho_masked.tif");

            jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, false, context);

            _ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
            _ddb.Verify(d => d.AddRaw("ortho_masked.tif"), Times.Once);
            _ddb.Verify(d => d.Build("ortho_masked.tif", null, false), Times.Once);
        }
        finally
        {
            File.Delete(written);
        }
    }

    [Test]
    public void MaskJob_MarkerWhoseFileIsGone_WritesTheOutputAgain()
    {
        // The earlier attempt wrote the output, which was deleted since: the marker no longer covers it
        var (jobs, _, context) = MaskJob(published: "ortho_masked.tif");

        jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, false, context);

        _ddb.Verify(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false, false), Times.Once);
        _ddb.Verify(d => d.AddRaw("ortho_masked.tif"), Times.Once);
    }

    [Test]
    public void MaskJob_StoredBeforeTheOverwriteParameter_RefusesAnOutputThatAppearedSinceTheRequest()
    {
        // Its request removed the output: the file there now is another writer's
        Entry("ortho_masked.tif");
        OnDisk("ortho_masked.tif");
        var (jobs, _, context) = MaskJob();

        Should.Throw<NameClashException>(() =>
                jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, context))
            .ConflictPath.ShouldBe("ortho_masked.tif");

        File.ReadAllText(Path.Combine(_root, "ortho_masked.tif")).ShouldBe(Previous);
        _ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public void MaskJob_StoredBeforeTheOverwriteParameter_WritesANewOutput_WithoutTheReplace()
    {
        var (jobs, _, context) = MaskJob();

        jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, context);

        _ddb.Verify(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false, false), Times.Once);
    }
}
