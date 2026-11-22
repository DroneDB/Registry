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
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
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
    private TestDb<RegistryContext> _db = null!;
    private RescanIndexTool _tool = null!;

    [SetUp]
    public void Setup()
    {
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/ds");
        _cacheInvalidator = new Mock<IDatasetCacheInvalidator>();
        _jobs = new Mock<IBackgroundJobsProcessor>();
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

    // The builds queued as an upload queues them, by path
    private List<string?> QueuedBuilds()
    {
        var paths = new List<string?>();
        foreach (var call in _jobs.Invocations.Where(i => i.Method.Name == nameof(IBackgroundJobsProcessor.EnqueueIndexed)))
            paths.Add(((Registry.Web.Models.IndexPayload)call.Arguments[1]!).Path);
        return paths;
    }

    // An unfiltered rescan whose rebuild of the dataset stops with stop; the dataset holds a.tif (buildable), notes.txt
    // (not buildable) and a folder
    private async Task<Exception> RebuildStopsWith(Exception stop)
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, null, false)))
            .Returns([new RescanResult { Path = "a.tif", Success = true, Hash = "h1" }]);
        _ddb.Setup(x => x.Build(null!, null, true)).Throws(stop);
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
    /// The rescan cleared the build cache, then its rebuild was cancelled: the builds of the entries are queued as their
    /// upload queued them, so the dataset gets its derived files back; the cancel is still the outcome of the task.
    /// </summary>
    [Test]
    public async Task Execute_NoFilter_ARebuildCancelledAfterTheClear_QueuesTheBuildsOfTheEntries()
    {
        var thrown = await RebuildStopsWith(new OperationCanceledException());

        thrown.ShouldBeAssignableTo<OperationCanceledException>();
        _ddb.Verify(x => x.ClearBuildCache(), Times.Once);
        QueuedBuilds().ShouldBe(["a.tif"]);
    }

    [Test]
    public async Task Execute_NoFilter_ARebuildThatFailsAfterTheClear_QueuesTheBuildsOfTheEntries()
    {
        var thrown = await RebuildStopsWith(new InvalidOperationException("cannot build"));

        thrown.ShouldBeOfType<InvalidOperationException>();
        QueuedBuilds().ShouldBe(["a.tif"]);
    }

    /// <summary>The dataset was deleted (the delete stopped its rescan): nothing is queued for it.</summary>
    [Test]
    public async Task Execute_NoFilter_ARebuildStoppedByTheDeleteOfTheDataset_QueuesNothing()
    {
        using (var context = _db.NewContext())
        {
            context.Datasets.RemoveRange(context.Datasets);
            context.SaveChanges();
        }

        await RebuildStopsWith(new OperationCanceledException());

        QueuedBuilds().ShouldBeEmpty();
    }

    [Test]
    public async Task Execute_ExtensionFilter_RebuildsOnlyRescannedBuildableEntries()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, "dwg", false)))
            .Returns([
                new RescanResult { Path = "site.dwg", Success = true, Hash = "h1" },
                new RescanResult { Path = "fails.dwg", Success = true, Hash = "h2" },
                new RescanResult { Path = "plain.dwg", Success = true, Hash = "h3" },
                new RescanResult { Path = "broken.dwg", Success = false, Error = "bad file" }
            ]);
        _ddb.Setup(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> paths) => paths
            .Select(p => p != "plain.dwg"
                ? TestBuilds.Build("/datasets/ds", p, EntryType.Vector, "h-" + p, BuildState.None)
                : TestBuilds.NotBuildable(p)).ToArray());
        _ddb.Setup(x => x.Build("fails.dwg", null, true)).Throws(new InvalidOperationException("cannot build"));

        await Execute(new { extensions = ".DWG" });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _ddb.Verify(x => x.Build("site.dwg", null, true), Times.Once);
        _ddb.Verify(x => x.Build("fails.dwg", null, true), Times.Once);
        _ddb.Verify(x => x.Build(It.Is<string>(p => p == "plain.dwg" || p == "broken.dwg" || p == null),
            It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
    }

    [Test]
    public async Task Execute_FilterWithoutMatches_TouchesNothing()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions("vector", null, false))).Returns([]);

        await Execute(new { types = "Vector" });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Never);
        _ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task Execute_NoFilter_ClearsBuildCacheAndRebuildsDataset()
    {
        _ddb.Setup(x => x.RescanIndex(new RescanOptions(null, null, false)))
            .Returns([new RescanResult { Path = "a.tif", Success = true, Hash = "h1" }]);

        await Execute(new { });

        _ddb.Verify(x => x.ClearBuildCache(), Times.Once);
        // A null path builds the whole dataset; the port declares the parameter non-nullable.
        _ddb.Verify(x => x.Build(null!, null, true), Times.Once);
        QueuedBuilds().ShouldBeEmpty("a rebuild that ends queues nothing more");
        _ddb.Verify(x => x.Build("a.tif", It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _cacheInvalidator.Verify(x => x.InvalidateAllDatasetCachesAsync("org", "ds"), Times.Once);
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
        var ctx = TestJobs.ExecutionContext(_ddb.Object, internalRef: DatasetRef);
        return _tool.ExecuteAsync(request, ctx, new Progress<HeavyToolProgress>(), CancellationToken.None);
    }
}
