using Registry.Web.Test.Support;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <c>rescan-entries</c>: a rescan of selected entries that validates its paths at submit, queues the rebuilds of the
/// rescanned buildable entries only, as build tasks of their own, and, unlike the rescan of the index, does not lock
/// the dataset.
/// </summary>
[TestFixture]
public class RescanEntriesToolTest
{
    private static readonly Guid DatasetRef = Guid.Parse("cccccccc-0000-0000-0000-000000000004");

    private Mock<IDDB> _ddb = null!;
    private Mock<IDatasetCacheInvalidator> _cacheInvalidator = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private List<QueuedDdbJob> _queued = null!;
    private List<string> _log = null!;
    private TestDb<RegistryContext> _db = null!;
    private RescanEntriesTool _tool = null!;

    [SetUp]
    public void Setup()
    {
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/ds");
        // The entries of the dataset: a.tif, folder/b.dwg and folder/notes.txt
        _ddb.Setup(x => x.GetEntry(It.IsAny<string>())).Returns((string path) =>
            path is "a.tif" or "folder" or "folder/b.dwg" or "folder/notes.txt" ? new Entry { Path = path } : null);
        _cacheInvalidator = new Mock<IDatasetCacheInvalidator>();
        _queued = [];
        _log = [];
        _jobs = QueuedDdbJobs.Capture(_queued);
        _db = TestDb.Create<RegistryContext>();
        using (var context = _db.NewContext())
        {
            Seed.Dataset(context, Seed.Organization(context, "org", ownerId: "owner"), "ds", DatasetRef);
            context.SaveChanges();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => TestJobs.Rebuild(_db.NewContext(), _jobs.Object, _cacheInvalidator.Object));
        _tool = new RescanEntriesTool(TestPaths.Policy,
            new RescanExecution(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()));
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private static readonly string[] FolderAndFile = ["folder", "a.tif"];
    private static readonly string[] OnlyFile = ["a.tif"];

    // The params of a request for these paths
    private static object Of(params string[] paths) => new { paths };

    private static object Of(bool stopOnError, params string[] paths) => new { paths, stopOnError };

    private static HeavyToolRequest Request(object prms, string? path = null) =>
        new("rescan-entries", "1", "org", "ds", path, JsonSerializer.SerializeToElement(prms));

    private Task Validate(object prms, string? path = null) => _tool.ValidateAsync(Request(prms, path),
        new HeavyToolValidationContext(_ddb.Object, null, NullLogger.Instance), CancellationToken.None);

    private Task<HeavyToolArtifact?> Execute(object prms)
    {
        var ctx = TestJobs.ExecutionContext(_ddb.Object, internalRef: DatasetRef, taskId: "rescan-task", jobs: _jobs.Object);
        return _tool.ExecuteAsync(Request(prms), ctx,
            new SyncProgress<HeavyToolProgress>(p => { if (p.LogChunk != null) _log.Add(p.LogChunk); }),
            CancellationToken.None);
    }

    [Test]
    public async Task Validate_EntriesThatExist_Pass()
    {
        await Validate(Of("a.tif", "/folder", "folder//b.dwg"));
    }

    [TestCase("{}")]
    [TestCase("{\"paths\":[]}")]
    [TestCase("{\"paths\":\"a.tif\"}")]
    public void Validate_NoPath_IsAnArgumentException(string prms)
    {
        var request = new HeavyToolRequest("rescan-entries", "1", "org", "ds", null, JsonDocument.Parse(prms).RootElement);

        Should.Throw<ArgumentException>(() => _tool.ValidateAsync(request,
            new HeavyToolValidationContext(_ddb.Object, null, NullLogger.Instance), CancellationToken.None));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("/")]
    [TestCase("a/../b")]
    public void Validate_TheDatasetRootOrAnInvalidPath_IsABadRequest(string path)
    {
        Should.Throw<InvalidDatasetPathException>(() => Validate(Of("a.tif", path)));
        _ddb.Verify(x => x.GetEntry(It.IsAny<string>()), Times.Never, "the paths are checked before the index is asked");
    }

    [Test]
    public void Validate_APathThatIsNotIndexed_IsNotFound()
    {
        var ex = Should.Throw<NotFoundException>(() => Validate(Of("a.tif", "missing.tif")));

        ex.Message.ShouldContain("missing.tif");
    }

    [Test]
    public void Validate_ARequestPath_IsRefusedInsteadOfIgnored()
    {
        Should.Throw<BadRequestException>(() => Validate(Of("a.tif"), path: "a.tif"));
    }

    [Test]
    public void TheTool_DoesNotLockTheDataset_AndHasItsOwnQuota()
    {
        DatasetActivity.LockingTools.ShouldNotContainKey(HeavyToolIds.RescanEntries);
        _tool.Id.ShouldBe("rescan-entries");
        _tool.Title.ShouldBe("Rescan entries");
        _tool.RequiredAccess.ShouldBe(HeavyToolPermission.Write);
        _tool.RunsNativeBuild.ShouldBeTrue();
        _tool.ProducesArtifact.ShouldBeFalse();
        _tool.Plan(Request(Of("a.tif")),
            new HeavyToolValidationContext(_ddb.Object, null, NullLogger.Instance)).QuotaKey.ShouldBe("rescan-entries");
        _tool.InputSchema.RootElement.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public void Label_IsThePathOfOneEntry_AndTheCountForMore()
    {
        _tool.Label(Request(Of("/folder//b.dwg"))).ShouldBe("folder/b.dwg");
        _tool.Label(Request(Of("a.tif", "folder"))).ShouldBe("2 entries");
        _tool.Label(Request(Of("a.tif", "/a.tif"))).ShouldBe("a.tif", "the same entry twice is one entry");
        _tool.Label(Request(new { })).ShouldBeNull();
        _tool.Label(Request(Of("../x"))).ShouldBeNull();
    }

    /// <summary>
    /// The rebuilds of the rescanned buildable entries only are queued, forced, as children of the rescan task: the
    /// rescan builds nothing itself, the build cache is not cleared and the dataset is not rebuilt as a whole, as in
    /// any rescan with a filter.
    /// </summary>
    [Test]
    public async Task Execute_RescansThePaths_AndQueuesTheRebuildsOfOnlyTheRescannedBuildableEntries()
    {
        RescanFolderAndFile();

        await Execute(Of(true, "/folder", "a.tif", "folder"));

        _ddb.Verify(x => x.RescanIndex(It.Is<RescanOptions>(o => o.StopOnError)), Times.Once);
        _queued.Select(j => (j.Method, j.Path, j.Force, j.Meta.ParentJobId)).ShouldBe([
            ("Build", "folder/b.dwg", true, "rescan-task"),
            ("Build", "a.tif", true, "rescan-task")
        ]);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
        _log.Last().ShouldBe("Rescan complete: 3/4 ok, 1 errors; 2 rebuilds queued");
    }

    /// <summary>A rebuild that cannot be queued does not stop the others, and the log says so; the rescan succeeds.</summary>
    [Test]
    public async Task Execute_ARebuildThatCannotBeQueued_DoesNotStopTheOthers()
    {
        RescanFolderAndFile();
        _jobs.Setup(x => x.EnqueueIndexed(It.IsAny<System.Linq.Expressions.Expression<Action<Registry.Web.Services.Jobs.DdbJobs>>>(),
                It.Is<Registry.Web.Models.IndexPayload>(m => m.Path == "folder/b.dwg")))
            .Throws(new InvalidOperationException("the queue is down"));

        await Execute(Of("folder", "a.tif"));

        _queued.Select(j => j.Path).ShouldBe(["a.tif"]);
        _log.Last().ShouldBe("Rescan complete: 3/4 ok, 1 errors; 1 rebuild queued, 1 could not be queued");
    }

    // The rescan of folder and a.tif: folder/b.dwg and a.tif are buildable, folder/notes.txt is not, missing.tif failed
    private void RescanFolderAndFile()
    {
        _ddb.Setup(x => x.RescanIndex(It.Is<RescanOptions>(o => o.Types == null && o.Extensions == null &&
                o.Paths != null && o.Paths.SequenceEqual(FolderAndFile))))
            .Returns([
                new RescanResult { Path = "folder/b.dwg", Success = true, Hash = "h1" },
                new RescanResult { Path = "folder/notes.txt", Success = true, Hash = "h2" },
                new RescanResult { Path = "a.tif", Success = true, Hash = "h3" },
                new RescanResult { Path = "missing.tif", Success = false, Error = "Not in index" }
            ]);
        _ddb.Setup(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> paths) => paths
            .Select(p => p != "folder/notes.txt"
                ? TestBuilds.Build("/data/ds", p, EntryType.Vector, "h-" + p, BuildState.None)
                : TestBuilds.NotBuildable(p)).ToArray());
    }

    [Test]
    public async Task Execute_NothingRescanned_TouchesNothing()
    {
        _ddb.Setup(x => x.RescanIndex(It.IsAny<RescanOptions>())).Returns([]);

        await Execute(Of("a.tif"));

        _ddb.Verify(x => x.RescanIndex(It.Is<RescanOptions>(o => !o.StopOnError && o.Paths!.SequenceEqual(OnlyFile))),
            Times.Once);
        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _queued.ShouldBeEmpty();
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
