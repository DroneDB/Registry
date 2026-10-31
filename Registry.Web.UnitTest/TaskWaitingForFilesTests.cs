using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// A build that finds files missing (an .obj without its .mtl) returns as libddb defers it, so its job succeeds: the
/// task lists say that the task waits for files, and which ones, while its entry waits for them. It is read from
/// libddb's pending markers when the list is made, with the rule of the build e-mails (the real
/// <see cref="Services.BuildNotifications.BuildOutcomeClassifier"/> here, over a mocked manifest), for the succeeded builds only and with one native
/// call per dataset.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TaskWaitingForFilesTests"
/// </summary>
[TestFixture]
public class TaskWaitingForFilesTests
{
    private const string Folder = "/data/datasets/org/ds";
    private const string Model = "house.obj";
    private const string ModelHash = "model-hash";
    private const string Mtl = "odm_textured_model_geo.mtl";

    private RegistryContext _db = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IBuildArtifactResolver> _builds = null!;
    private readonly Dictionary<string, EntryBuildArtifacts> _entries = [];
    private int _opened;

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.CreateContext<RegistryContext>();
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([]);
        _builds = new Mock<IBuildArtifactResolver>();
        _builds.Setup(b => b.GetBuilds(It.IsAny<IDDB>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns<IDDB, IReadOnlyList<string>>((_, paths) =>
                [.. paths.Select(p => _entries.TryGetValue(p, out var e) ? e : new EntryBuildArtifacts { Path = p })]);
        _entries.Clear();
        _opened = 0;
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private void AddTask(string id, string? path, string state = "Succeeded", string toolId = "build",
        string? hash = ModelHash, string ds = "ds", int minutesAgo = 0)
    {
        _db.JobIndices.Add(new JobIndex
        {
            JobId = id, OrgSlug = "org", DsSlug = ds, Path = path, Hash = hash, ToolId = toolId, ToolVersion = "1",
            CurrentState = state, CreatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo)
        });
        _db.SaveChanges();
    }

    // The model's build found its .mtl missing: libddb keeps the marker of this content, waiting for it
    private void ModelWaitsForItsMtl()
    {
        _entries[Model] = TestBuilds.Pending(Folder, Model, EntryType.Model, ModelHash, Mtl);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns(
        [
            new PendingBuildInfo { Path = Model, Hash = ModelHash, MissingDependencies = [Mtl], AwaitingFiles = true }
        ]);
    }

    private BuildTaskWaits Waits(IDdbManager? ddbManager = null) =>
        TestJobs.BuildTaskWaits(_db, _builds.Object, ddbManager);

    private TaskHistory History(IRecordCache? cache = null)
    {
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync("org", "ds", AccessType.Read, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatasetAccess(
                new Dataset { Slug = "ds", Organization = new Organization { Slug = "org" } },
                () =>
                {
                    _opened++;
                    return _ddb.Object;
                }));
        var auth = new Mock<IAuthManager>();
        return new TaskHistory(access.Object, new JobIndexQuery(_db), cache ?? Mock.Of<IRecordCache>(),
            new TaskAuthorizer(access.Object, new JobIndexQuery(_db), auth.Object), null!, Waits(),
            TestSettings.Options<ProcessingPlatformSettings>(new AppSettings()));
    }

    private static IRecordCache RecordCache() =>
        new ServiceCollection().AddLogging().AddDistributedMemoryCache().AddRecordCache().BuildServiceProvider()
            .GetRequiredService<IRecordCache>();

    // The canonical page (no filter: what the dataset's Tasks tab asks for) is cached; a filtered one is not
    private async Task<Models.DTO.TaskSummaryDto[]> ListAsync(bool canonical) =>
        (await (canonical ? History(RecordCache()) : History())
            .ListPageAsync("org", "ds", canonical ? null : "build", null, 0, 50)).Items;

    [TestCase(true)]
    [TestCase(false)]
    public async Task List_ASucceededBuildWhoseEntryWaitsForFiles_WaitsForThem_AndNoOtherTask(bool canonical)
    {
        ModelWaitsForItsMtl();
        _entries["ortho.tif"] = TestBuilds.Build(Folder, "ortho.tif", EntryType.GeoRaster, "ortho-hash",
            BuildState.Complete);
        AddTask("waits", Model);
        // An earlier content of the model, built then: its task does not wait for the file of the new one
        AddTask("earlier", Model, hash: "earlier-hash", minutesAgo: 60);
        AddTask("built", "ortho.tif", hash: "ortho-hash");
        AddTask("failed", "broken.laz", state: "Failed", hash: "laz-hash");
        AddTask("running", "cloud.laz", state: "Processing", hash: "cloud-hash");

        var page = await ListAsync(canonical);

        page.Single(t => t.TaskId == "waits").ShouldSatisfyAllConditions(
            t => t.WaitingForFiles.ShouldBeTrue(),
            t => t.MissingFiles.ShouldBe([Mtl]));
        page.Where(t => t.TaskId != "waits").ShouldAllBe(t => !t.WaitingForFiles && t.MissingFiles == null);
        // Only the succeeded builds are looked up: one manifest call and one marker read for the page
        _builds.Verify(b => b.GetBuilds(_ddb.Object,
            It.Is<IReadOnlyList<string>>(p => p.Count == 2 && p.Contains(Model) && p.Contains("ortho.tif"))), Times.Once);
        _ddb.Verify(d => d.GetPendingBuildInfo(), Times.Once);
    }

    [Test]
    public async Task List_TheFileArrivedAndTheEntryIsBuilt_TheTaskNoLongerWaits()
    {
        _entries[Model] = TestBuilds.Build(Folder, Model, EntryType.Model, ModelHash, BuildState.Complete);
        AddTask("waited", Model);

        var page = await ListAsync(canonical: false);

        page.ShouldHaveSingleItem().WaitingForFiles.ShouldBeFalse();
        // A complete build needs no marker
        _ddb.Verify(d => d.GetPendingBuildInfo(), Times.Never);
    }

    [Test]
    public async Task List_WithoutASucceededBuild_DoesNotOpenTheIndex()
    {
        AddTask("failed", "broken.laz", state: "Failed");
        AddTask("extract", "photos.zip", toolId: "archive-extract");
        AddTask("pending", null);

        var page = await ListAsync(canonical: true);

        page.Length.ShouldBe(3);
        page.ShouldAllBe(t => !t.WaitingForFiles);
        _opened.ShouldBe(0);
        _builds.Verify(b => b.GetBuilds(It.IsAny<IDDB>(), It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Test]
    public async Task List_AnIndexThatCannotBeRead_ListsTheTasksAsTheyAre()
    {
        _builds.Setup(b => b.GetBuilds(It.IsAny<IDDB>(), It.IsAny<IReadOnlyList<string>>()))
            .Throws(new DdbException("database is locked"));
        AddTask("waits", Model);

        var page = await ListAsync(canonical: false);

        page.ShouldHaveSingleItem().WaitingForFiles.ShouldBeFalse();
    }

    [Test]
    public async Task Status_OfASucceededBuildWhoseEntryWaitsForFiles_WaitsForThem()
    {
        ModelWaitsForItsMtl();
        AddTask("waits", Model);

        var status = await History().GetStatusAsync("org", "ds", "waits");

        status.WaitingForFiles.ShouldBeTrue();
        status.MissingFiles.ShouldBe([Mtl]);
    }

    [Test]
    public async Task FindAsync_TasksOfSeveralDatasets_OpensEachIndexOnce_AndSkipsADeletedDataset()
    {
        var org = Seed.Organization(_db, "org");
        var first = Seed.Dataset(_db, org, "ds");
        var second = Seed.Dataset(_db, org, "other");
        _db.SaveChanges();
        ModelWaitsForItsMtl();
        var otherDdb = new Mock<IDDB>();
        otherDdb.Setup(d => d.GetPendingBuildInfo()).Returns([]);
        var manager = new Mock<IDdbManager>();
        manager.Setup(m => m.Open("org", first.InternalRef)).Returns(_ddb.Object);
        manager.Setup(m => m.Open("org", second.InternalRef)).Returns(otherDdb.Object);
        AddTask("waits", Model);
        AddTask("waits-too", Model, minutesAgo: 5);
        AddTask("other", "ortho.tif", ds: "other");
        AddTask("deleted", Model, ds: "gone");

        var waits = await Waits(manager.Object).FindAsync([.. _db.JobIndices]);

        waits.MissingFilesOf("waits").ShouldBe([Mtl]);
        waits.MissingFilesOf("waits-too").ShouldBe([Mtl]);
        waits.MissingFilesOf("other").ShouldBeNull();
        waits.MissingFilesOf("deleted").ShouldBeNull();
        manager.Verify(m => m.Open("org", first.InternalRef), Times.Once);
        manager.Verify(m => m.Open("org", second.InternalRef), Times.Once);
        manager.Verify(m => m.Open(It.IsAny<string>(), It.IsAny<Guid>()), Times.Exactly(2));
    }
}
