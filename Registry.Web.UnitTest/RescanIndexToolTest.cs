using Registry.Web.Test.Support;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Ports;
using Shouldly;

namespace Registry.Web.Test;

[TestFixture]
public class RescanIndexToolTest
{
    private static readonly Guid DatasetRef = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private Mock<IDDB> _ddb = null!;
    private Mock<IDatasetCacheInvalidator> _cacheInvalidator = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private List<QueuedDdbJob> _queued = null!;
    private List<string> _log = null!;
    private TestDb<RegistryContext> _db = null!;
    private RescanIndexTool _tool = null!;

    [SetUp]
    public void Setup()
    {
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/ds");
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
        _tool = new RescanIndexTool(
            new RescanExecution(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()));
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    // An unfiltered rescan whose rebuild of the dataset cannot be queued; the dataset holds a.tif (buildable),
    // notes.txt (not buildable) and a folder
    private async Task<Exception> DatasetRebuildCannotBeQueued()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, null, false)))
            .Returns([new RescanResult { Path = "a.tif", Success = true, Hash = "h1" }]);
        _jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
                It.Is<IndexPayload>(m => m.Path == null)))
            .Throws(new InvalidOperationException("the queue is down"));
        _ddb.Setup(x => x.FindSummaries(EntryQuery.All)).Returns([
            new EntrySummary { Path = "a.tif", Type = EntryType.GeoRaster, Hash = "h1" },
            new EntrySummary { Path = "notes.txt", Type = EntryType.Generic, Hash = "h2" },
            new EntrySummary { Path = "folder", Type = EntryType.Directory }
        ]);
        _ddb.Setup(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> paths) => paths
            .Select(p => p == "a.tif"
                ? TestBuilds.Build("/data/ds", p, EntryType.GeoRaster, "h1", BuildState.None)
                : TestBuilds.NotBuildable(p)).ToArray());

        return await Should.ThrowAsync<Exception>(() => Execute(new { }));
    }

    /// <summary>
    /// The rescan cleared the build cache, then the rebuild of the dataset could not be queued: the builds of the
    /// entries are queued as their upload queued them, so the dataset gets its derived files back; the failure is
    /// still the outcome of the task.
    /// </summary>
    [Test]
    public async Task Execute_NoFilter_TheRebuildOfTheDatasetCannotBeQueued_QueuesTheBuildsOfTheEntries()
    {
        var thrown = await DatasetRebuildCannotBeQueued();

        thrown.ShouldBeOfType<InvalidOperationException>();
        _ddb.Verify(x => x.ClearBuildCache(), Times.Once);
        _queued.Select(j => (j.Path, j.Force, j.Meta.ParentJobId)).ShouldBe([("a.tif", false, (string?)null)]);
    }

    /// <summary>The dataset was deleted (the delete stopped its rescan): nothing is queued for it.</summary>
    [Test]
    public async Task Execute_NoFilter_TheRebuildOfADeletedDatasetCannotBeQueued_QueuesNothing()
    {
        using (var context = _db.NewContext())
        {
            context.Datasets.RemoveRange(context.Datasets);
            context.SaveChanges();
        }

        await DatasetRebuildCannotBeQueued();

        _queued.ShouldBeEmpty();
    }

    /// <summary>
    /// A filtered rescan queues the forced rebuilds of the rescanned buildable entries only, as children of the rescan
    /// task; it builds nothing itself and keeps the build cache.
    /// </summary>
    [Test]
    public async Task Execute_ExtensionFilter_QueuesTheRebuildsOfOnlyTheRescannedBuildableEntries()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, "dwg", false)))
            .Returns([
                new RescanResult { Path = "site.dwg", Success = true, Hash = "h1" },
                new RescanResult { Path = "other.dwg", Success = true, Hash = "h2" },
                new RescanResult { Path = "plain.dwg", Success = true, Hash = "h3" },
                new RescanResult { Path = "broken.dwg", Success = false, Error = "bad file" }
            ]);
        _ddb.Setup(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> paths) => paths
            .Select(p => p != "plain.dwg"
                ? TestBuilds.Build("/datasets/ds", p, EntryType.Vector, "h-" + p, BuildState.None)
                : TestBuilds.NotBuildable(p)).ToArray());

        await Execute(new { extensions = ".DWG" });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _queued.Select(j => (j.Method, j.Path, j.Force, j.Meta.ParentJobId)).ShouldBe([
            ("Build", "site.dwg", true, "rescan-task"),
            ("Build", "other.dwg", true, "rescan-task")
        ]);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
        _log.Last().ShouldBe("Rescan complete: 3/4 ok, 1 errors; 2 rebuilds queued");
    }

    [Test]
    public async Task Execute_FilterWithoutMatches_TouchesNothing()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions("vector", null, false))).Returns([]);

        await Execute(new { types = "Vector" });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _queued.ShouldBeEmpty();
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// An unfiltered rescan clears the build cache and queues one forced rebuild of the dataset, a child of the rescan
    /// task: the rescan builds nothing itself, so a rebuild that fails is a failed build, not a failed rescan.
    /// </summary>
    [Test]
    public async Task Execute_NoFilter_ClearsTheBuildCacheAndQueuesTheRebuildOfTheDataset()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, null, false)))
            .Returns([new RescanResult { Path = "a.tif", Success = true, Hash = "h1" }]);

        await Execute(new { });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Once);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _queued.Select(j => (j.Method, j.Path, j.Force, j.Meta.ParentJobId))
            .ShouldBe([("Build", (string?)null, true, "rescan-task")]);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
        _log.Last().ShouldBe("Rescan complete: 1/1 ok, 0 errors; rebuild of the dataset queued");
    }

    [TestCase("{\"types\":\"nope\"}")]
    [TestCase("{\"extensions\":\"*.dwg\"}")]
    public void Validate_InvalidFilter_ThrowsArgumentException(string prms)
    {
        var request = new HeavyToolRequest("rescan-index", "1", "org", "ds", null,
            JsonDocument.Parse(prms).RootElement);

        Should.Throw<ArgumentException>(() => _tool.ValidateAsync(request,
            new HeavyToolValidationContext(_ddb.Object, null, NullLogger.Instance), CancellationToken.None));
    }

    /// <summary>A path of the request would be ignored by a rescan of the index: it is refused, and names rescan-entries.</summary>
    [Test]
    public void Validate_ARequestPath_IsRefusedInsteadOfIgnored()
    {
        var request = new HeavyToolRequest("rescan-index", "1", "org", "ds", "a.tif", JsonSerializer.SerializeToElement(new { }));

        var ex = Should.Throw<BadRequestException>(() => _tool.ValidateAsync(request,
            new HeavyToolValidationContext(_ddb.Object, null, NullLogger.Instance), CancellationToken.None));
        ex.Message.ShouldContain("rescan-entries");
    }

    private Task<HeavyToolArtifact?> Execute(object prms)
    {
        var request = new HeavyToolRequest("rescan-index", "1", "org", "ds", null,
            JsonSerializer.SerializeToElement(prms));
        var ctx = TestJobs.ExecutionContext(_ddb.Object, internalRef: DatasetRef, taskId: "rescan-task", jobs: _jobs.Object);
        return _tool.ExecuteAsync(request, ctx,
            new SyncProgress<HeavyToolProgress>(p => { if (p.LogChunk != null) _log.Add(p.LogChunk); }),
            CancellationToken.None);
    }
}
