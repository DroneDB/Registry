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
/// replace goes to the existing entry (any letter case); the mask job checks again when it runs, records the written
/// output (<see cref="PublishedOutputMarker"/>) and a later attempt only indexes and builds it; a job stored before
/// the <c>overwrite</c> parameter keeps replacing.
/// </summary>
[TestFixture]
public class RasterServicesOutputTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string Folder = "/datasets/org/ds";

    private Mock<IDDB> _ddb = null!;
    private Mock<IFileSystem> _fs = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private List<Job> _enqueued = null!;
    private List<EntrySummary> _entries = null!;
    private ObjectServices _services = null!;
    private Guid _ref;
    private readonly List<InMemoryStorage> _storages = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var storage in _storages)
            storage.Dispose();
        _storages.Clear();
    }

    [SetUp]
    public void SetUp()
    {
        _entries = [];
        _enqueued = [];

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
        _ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Folder + "/" + p);
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

        _services = new ObjectServicesBuilder(TestDb.CreateContext<RegistryContext>())
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
        _ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>()), Times.Never);
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
        _ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Merge_WithOverwrite_ReplacesTheExistingEntry()
    {
        Entry("b1.tif");
        Entry("b2.tif");
        Entry("Merged.tif");

        await _services.Processing.MergeMultispectralAsync(Org, Ds, ["b1.tif", "b2.tif"], "merged.tif", overwrite: true);

        _ddb.Verify(d => d.MergeMultispectral(new[] { "b1.tif", "b2.tif" }, "Merged.tif"), Times.Once);
        _ddb.Verify(d => d.AddRaw("Merged.tif"), Times.Once);
    }

    // ---- Mask borders (request) ----

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

        _ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public void MaskJob_RecordsTheWrittenOutput_BeforeIndexingIt()
    {
        var order = new List<string>();
        var (jobs, connection, context) = MaskJob();
        _ddb.Setup(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false)).Callback(() => order.Add("mask"));
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

            _ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
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

        _ddb.Verify(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false), Times.Once);
        _ddb.Verify(d => d.AddRaw("ortho_masked.tif"), Times.Once);
    }

    [Test]
    public void MaskJob_StoredBeforeTheOverwriteParameter_KeepsReplacing()
    {
        Entry("ortho_masked.tif");
        var (jobs, _, context) = MaskJob();

        jobs.MaskBorders(Org, _ref, "ortho.tif", "ortho_masked.tif", 15, false, context);

        _ddb.Verify(d => d.MaskBorders("ortho.tif", "ortho_masked.tif", 15, false), Times.Once);
    }
}
