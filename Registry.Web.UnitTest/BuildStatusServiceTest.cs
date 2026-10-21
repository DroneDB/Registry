using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hangfire.InMemory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

/// <summary>
/// Status rules of <see cref="BuildStatusService"/> ("building" &gt; "pending" &gt; ready &gt;
/// "failed" &gt; "queued") and their data access: one native batch status call
/// (<see cref="IDdbBuild.GetBuildStatus"/>) and job index queries scoped to the listed
/// paths, run against the real <see cref="JobIndexQuery"/> on an in-memory context.
/// </summary>
[TestFixture]
public class BuildStatusServiceTest : TestBase
{
    private RegistryContext _context;
    private JobIndexQuery _jobIndexQuery;
    private Mock<IDDB> _ddbMock;
    private ILogger<BuildStatusService> _logger;
    private Dictionary<string, EntryBuildStatus> _native;
    private List<string[]> _statusCalls;
    private int _jobSeq;

    private const string OrgSlug = "org";
    private const string DsSlug = "ds";

    [SetUp]
    public void Setup()
    {
        _context = TestDb.CreateContext<RegistryContext>();
        _jobIndexQuery = new JobIndexQuery(_context);
        _ddbMock = new Mock<IDDB>();
        _logger = CreateTestLogger<BuildStatusService>();
        _native = new Dictionary<string, EntryBuildStatus>();
        _statusCalls = [];
        _jobSeq = 0;

        _ddbMock.Setup(x => x.GetPendingBuildInfo()).Returns([]);
        _ddbMock.Setup(x => x.GetBuildStatus(It.IsAny<string[]>()))
            .Returns((string[] paths) =>
            {
                _statusCalls.Add(paths);
                return paths.Select(p => _native.TryGetValue(p, out var s)
                    ? s
                    : new EntryBuildStatus { Path = p }).ToArray();
            });
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private BuildStatusService CreateService() => new(_jobIndexQuery, _logger);

    private static EntryDto Entry(string path, EntryType type = EntryType.GeoRaster) =>
        new() { Path = path, Type = type };

    private void Native(string path, bool buildable = true, bool complete = false) =>
        _native[path] = new EntryBuildStatus { Path = path, Exists = true, Buildable = buildable, Complete = complete };

    private JobIndex AddJob(string path, string state, DateTime? createdAt = null, string toolId = "build",
        string dsSlug = DsSlug)
    {
        var job = new JobIndex
        {
            JobId = "j" + ++_jobSeq, OrgSlug = OrgSlug, DsSlug = dsSlug, Path = path, ToolId = toolId,
            CurrentState = state, CreatedAtUtc = createdAt ?? DateTime.UtcNow
        };
        _context.JobIndices.Add(job);
        _context.SaveChanges();
        return job;
    }

    [Test]
    public async Task AnnotateAsync_NoBuildableEntries_SkipsAllLookups()
    {
        var entries = new[] { Entry("readme.txt", EntryType.Markdown), Entry("photo.jpg", EntryType.Image) };

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, entries);

        entries.ShouldAllBe(e => e.BuildStatus == null);
        _statusCalls.ShouldBeEmpty();
        _ddbMock.Verify(x => x.GetPendingBuildInfo(), Times.Never);
    }

    [Test]
    public async Task AnnotateAsync_ActiveBuildJob_MarksBuilding()
    {
        var entry = Entry("ortho.tif");
        Native("ortho.tif");
        AddJob("ortho.tif", "Processing");

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("building");
        entry.BuildMissingDependencies.ShouldBeNull();
    }

    [Test]
    public async Task AnnotateAsync_ActiveJobOfAnotherToolOrDataset_IsNotBuilding()
    {
        var entry = Entry("ortho.tif");
        Native("ortho.tif", complete: true);
        AddJob("ortho.tif", "Processing", toolId: "raster-export");
        AddJob("ortho.tif", "Processing", dsSlug: "other");

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBeNull();
    }

    [Test]
    public async Task AnnotateAsync_PendingWithMissingDeps_MarksPendingAndSurfacesDeps()
    {
        var entry = Entry("layer.shp", EntryType.Vector);
        Native("layer.shp", complete: false);
        _ddbMock.Setup(x => x.GetPendingBuildInfo()).Returns([
            new PendingBuildInfo
            {
                Hash = "h1", Path = "layer.shp", MissingDependencies = ["layer.dbf"], LastAttempt = 123
            }
        ]);
        AddJob("layer.shp", "Failed");

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("pending");
        entry.BuildMissingDependencies.ShouldBe(["layer.dbf"]);
    }

    [Test]
    public async Task AnnotateAsync_BuildComplete_LeavesStatusNull()
    {
        var entry = Entry("ortho.tif");
        Native("ortho.tif", complete: true);
        AddJob("ortho.tif", "Failed");

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBeNull();
        entry.BuildMissingDependencies.ShouldBeNull();
    }

    [Test]
    public async Task AnnotateAsync_LastJobFailedAndIncomplete_MarksFailed()
    {
        var entry = Entry("ortho.tif");
        Native("ortho.tif", complete: false);
        AddJob("ortho.tif", "Succeeded", DateTime.UtcNow.AddHours(-1));
        AddJob("ortho.tif", "Failed", DateTime.UtcNow);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("failed");
    }

    [Test]
    public async Task AnnotateAsync_OlderFailureThenNewerSuccess_MarksQueued()
    {
        var entry = Entry("ortho.tif");
        Native("ortho.tif", complete: false);
        AddJob("ortho.tif", "Failed", DateTime.UtcNow.AddHours(-1));
        AddJob("ortho.tif", "Succeeded", DateTime.UtcNow);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("queued");
    }

    [Test]
    public async Task AnnotateAsync_NothingYet_MarksQueued()
    {
        var entry = Entry("ortho.tif");
        Native("ortho.tif", complete: false);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBe("queued");
    }

    [Test]
    public async Task AnnotateAsync_NotBuildableEntry_LeavesStatusNull()
    {
        var entry = Entry("weird.tif");
        Native("weird.tif", buildable: false);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBeNull();
        _ddbMock.Verify(x => x.GetPendingBuildInfo(), Times.Never);
    }

    [Test]
    public async Task AnnotateAsync_NativeStatusFails_LeavesStatusesNull()
    {
        var entry = Entry("ortho.tif");
        _ddbMock.Setup(x => x.GetBuildStatus(It.IsAny<string[]>())).Throws(new InvalidOperationException("boom"));

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);

        entry.BuildStatus.ShouldBeNull();
    }

    [Test]
    public async Task AnnotateAsync_200Rasters_UsesOneNativeBatchCall()
    {
        var entries = Enumerable.Range(0, 200).Select(i => Entry($"ortho_{i}.tif")).ToList();
        foreach (var e in entries)
            Native(e.Path, complete: true);
        // Job history of other entries of the dataset is not read
        for (var i = 0; i < 50; i++)
            AddJob($"elsewhere_{i}.tif", "Succeeded");

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, entries);

        _statusCalls.Count.ShouldBe(1);
        _statusCalls[0].Length.ShouldBe(200);
        _ddbMock.Verify(x => x.GetPendingBuildInfo(), Times.Once);
        _ddbMock.Verify(x => x.IsBuildable(It.IsAny<string>()), Times.Never);
        _ddbMock.Verify(x => x.IsBuildComplete(It.IsAny<string>()), Times.Never);
        entries.ShouldAllBe(e => e.BuildStatus == null);
    }

    [Test]
    public async Task AnnotateAsync_MixedListing_EachRuleApplies()
    {
        var building = Entry("a.tif");
        var pending = Entry("b.obj", EntryType.Model);
        var ready = Entry("c.laz", EntryType.PointCloud);
        var failed = Entry("d.tif");
        var queued = Entry("e.tif");
        var image = Entry("f.jpg", EntryType.GeoImage);
        Native("a.tif");
        Native("b.obj");
        Native("c.laz", complete: true);
        Native("d.tif");
        Native("e.tif");
        AddJob("a.tif", "Enqueued");
        AddJob("d.tif", "Failed");
        _ddbMock.Setup(x => x.GetPendingBuildInfo()).Returns([
            new PendingBuildInfo { Hash = "h", Path = "b.obj", MissingDependencies = ["b.mtl"] }
        ]);

        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object,
            [building, pending, ready, failed, queued, image]);

        building.BuildStatus.ShouldBe("building");
        pending.BuildStatus.ShouldBe("pending");
        ready.BuildStatus.ShouldBeNull();
        failed.BuildStatus.ShouldBe("failed");
        queued.BuildStatus.ShouldBe("queued");
        image.BuildStatus.ShouldBeNull();
        _statusCalls.Single().ShouldBe(["a.tif", "b.obj", "c.laz", "d.tif", "e.tif"]);
    }

    [Test]
    public async Task AnnotateAsync_PendingRetryFailsForNonDependencyReason_MarksFailed()
    {
        // A pending OBJ was deferred by its original (succeeded) build job. The retry is the
        // indexed BuildWrapper enqueued by HangfireUtils.BuildPendingWrapper; build the JobIndex
        // row from that exact payload so the Path BuildStatusService matches on is the real one.
        const string path = "sub/model.obj";
        var entry = Entry(path, EntryType.Model);
        Native(path, complete: false);
        _ddbMock.Setup(x => x.GetPendingBuildInfo()).Returns([
            new PendingBuildInfo { Hash = "h1", Path = path, MissingDependencies = ["sub/model.mtl"], LastAttempt = 0 }
        ]);

        IndexPayload retryMeta = null;
        var jobs = new Mock<IBackgroundJobsProcessor>();
        jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Action>, IndexPayload>((_, meta) => retryMeta = meta)
            .Returns("retry-1");
        AddJob(path, "Succeeded", DateTime.UtcNow.AddMinutes(-5));

        HangfireUtils.BuildPendingWrapper(_ddbMock.Object, null, jobs.Object, _jobIndexQuery,
            OrgSlug, DsSlug, () => 1_800_000_000, new InMemoryStorage());

        retryMeta.ShouldNotBeNull();
        var retry = new JobIndex
        {
            JobId = "retry-1", OrgSlug = retryMeta.OrgSlug, DsSlug = retryMeta.DsSlug, Hash = retryMeta.Hash,
            Path = retryMeta.Path, ToolId = retryMeta.ToolId, CurrentState = "Processing",
            CreatedAtUtc = DateTime.UtcNow
        };
        _context.JobIndices.Add(retry);
        _context.SaveChanges();

        // While the retry runs it is "building".
        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);
        entry.BuildStatus.ShouldBe("building");

        // The attempt consumed ".pending" and failed for a non-dependency reason: "failed".
        _ddbMock.Setup(x => x.GetPendingBuildInfo()).Returns([]);
        retry.CurrentState = "Failed";
        _context.SaveChanges();
        entry.BuildStatus = null;
        await CreateService().AnnotateAsync(OrgSlug, DsSlug, _ddbMock.Object, [entry]);
        entry.BuildStatus.ShouldBe("failed");
    }
}
