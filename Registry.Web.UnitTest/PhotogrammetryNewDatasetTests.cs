using Registry.Web.Test.Support;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Archives;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.NodeOdx;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Photogrammetry "Create new dataset for results" (FR-PRC-03, D-031, AC-007): the worker creates the dataset when it
/// imports the results, by the system path (it has no user, which is what the old code stumbled on with a
/// ForbiddenException after the whole run); the submit checks the slug rule and the organization access; nothing is
/// created in the validation.
/// </summary>
[TestFixture]
public class PhotogrammetryNewDatasetTests
{
    private const string SlugRuleText =
        "Dataset slug is invalid: it has 2 to 128 characters (lowercase letters, digits, '_' and '-'), " +
        "starts with a letter or digit and has no '--'";

    private string _dir = null!;
    private TestDb<RegistryContext> _db = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private Mock<IDDB> _targetDdb = null!;
    private Mock<Registry.Ports.DroneDB.IMetaManager> _targetMeta = null!;
    private Mock<IDatasetAccessResolver> _access = null!;
    private Mock<INodeOdxClient> _node = null!;
    private Mock<IUserDatasetAccess> _users = null!;
    private Guid _createdRef;

    // The user who requested the task (JobIndex.UserId), whom the worker checks (D-031)
    private const string Requester = "requester";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ddb_photogrammetry_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_dir, "source"));
        Directory.CreateDirectory(Path.Combine(_dir, "target"));

        _db = TestDb.Create<RegistryContext>();
        using (var seed = _db.NewContext())
        {
            var org = new Organization { Slug = "r3photo", Name = "R3 photo", CreationDate = DateTime.UtcNow };
            seed.Organizations.Add(org);
            seed.Organizations.Add(new Organization { Slug = "other", Name = "Other", CreationDate = DateTime.UtcNow });
            seed.Datasets.Add(new Dataset
                { Slug = "taken", Organization = org, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow });
            seed.SaveChanges();
        }

        _targetMeta = new Mock<Registry.Ports.DroneDB.IMetaManager>();
        _targetDdb = new Mock<IDDB>();
        _targetDdb.SetupGet(d => d.DatasetFolderPath).Returns(Path.Combine(_dir, "target"));
        _targetDdb.SetupGet(d => d.Meta).Returns(_targetMeta.Object);
        _targetDdb.Setup(d => d.GetInfo()).Returns(new Registry.Ports.DroneDB.Entry { Properties = new Dictionary<string, JsonElement>() });
        _targetDdb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>())).Returns([]);

        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Callback<string, Guid>((_, internalRef) => _createdRef = internalRef)
            .Returns(_targetDdb.Object);

        _access = new Mock<IDatasetAccessResolver>();
        _node = new Mock<INodeOdxClient>();

        // The requester may write to both organizations, by the rules of the API read without a request
        _users = new Mock<IUserDatasetAccess>();
        _users.Setup(u => u.CanAccessOrganizationAsync(Requester, It.IsAny<string>(), AccessType.Write,
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // best-effort
        }
    }

    private PhotogrammetryTool CreateTool(bool withAccessResolver)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddSingleton(_ddbManager.Object);
        services.AddScoped<DatasetCreation>();
        // Every job host registers it (AddBuildNotifications): the access of a given user, without a request
        services.AddSingleton(_users.Object);
        // The processing node has no user: it registers no access resolver
        if (withAccessResolver)
            services.AddSingleton(_access.Object);
        var provider = services.BuildServiceProvider();

        var settings = new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings
            {
                NodeOdx = [new NodeOdxNodeConfig { Id = "n1", Url = "http://localhost:3000" }]
            }
        };
        var platform = TestSettings.Options<ProcessingPlatformSettings>(Microsoft.Extensions.Options.Options.Create(settings));
        var runner = new NodeOdxTaskRunner(_node.Object, new NodeOdxNodeRegistry(platform),
            TestScratch.For(Path.Combine(_dir, "scratch")), platform);

        return new PhotogrammetryTool(
            runner, TestArchives.Extractor(new SharpCompressArchiveExtractor()),
            provider.GetRequiredService<IServiceScopeFactory>(),
            TestSettings.Options<ImportSettings>(Microsoft.Extensions.Options.Options.Create(settings)), platform,
            TestPaths.Policy, TestJobs.FileIndexer());
    }

    private Mock<IDDB> SourceDdb()
    {
        var source = new Mock<IDDB>();
        source.SetupGet(d => d.DatasetFolderPath).Returns(Path.Combine(_dir, "source"));
        var images = new[] { "a.jpg", "b.jpg" }.Select(p => new Registry.Ports.DroneDB.Entry { Path = p, Type = EntryType.Image, Size = 100 }).ToList();
        source.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns(images);
        source.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(_dir, "source", p));
        return source;
    }

    private static HeavyToolRequest Request(object prms) =>
        new("photogrammetry", "2", "r3photo", "phalaborwa", null, JsonSerializer.SerializeToElement(prms));

    private static IHeavyToolValidationContext Validation(IDDB ddb, bool withCaller)
    {
        var ctx = new Mock<IHeavyToolValidationContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ddb);
        ctx.SetupGet(c => c.Caller).Returns(withCaller ? new ClaimsPrincipal(new ClaimsIdentity()) : null);
        return ctx.Object;
    }

    private int StoredDatasets()
    {
        using var db = _db.NewContext();
        return db.Datasets.Count();
    }

    // FR-PRC-03: the slug check is the Registry's one (SlugRules), at submit. The old local kebab-case regex let
    // "a--b" and a single "a" through (the creation then failed 400 at the end of the run) and refused "my_results"
    [TestCase("a--b")]
    [TestCase("a")]
    [TestCase("Bad Slug")]
    [TestCase("UPPER")]
    [TestCase("-start")]
    public void ValidateAsync_AnInvalidSlug_IsRefusedWithTheRuleOfTheRegistry(string slug)
    {
        var tool = CreateTool(withAccessResolver: true);

        var ex = Should.Throw<ArgumentException>(async () => await tool.ValidateAsync(
            Request(new { createNewDataset = true, newDatasetName = slug }),
            Validation(SourceDdb().Object, withCaller: true), CancellationToken.None));

        ex.Message.ShouldBe(SlugRuleText);
        StoredDatasets().ShouldBe(1);
    }

    [Test]
    public async Task ValidateAsync_AnUnderscoreSlug_IsAccepted()
    {
        var tool = CreateTool(withAccessResolver: true);

        await tool.ValidateAsync(Request(new { createNewDataset = true, newDatasetName = "my_results" }),
            Validation(SourceDdb().Object, withCaller: true), CancellationToken.None);
    }

    [Test]
    public void ValidateAsync_ATakenSlug_FailsFastNamingTheSlugAndCreatesNothing()
    {
        var tool = CreateTool(withAccessResolver: true);

        var ex = Should.Throw<ConflictException>(async () => await tool.ValidateAsync(
            Request(new { createNewDataset = true, newDatasetName = "taken" }),
            Validation(SourceDdb().Object, withCaller: true), CancellationToken.None));

        ex.Message.ShouldBe("A dataset named \"taken\" already exists in \"r3photo\"");
        ex.StatusCode.ShouldBe(409);
        StoredDatasets().ShouldBe(1);
        _ddbManager.Verify(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    // ---- The retry of a run that created its dataset (D-037): its own dataset is not a taken slug ----

    private static readonly Guid RunDatasetRef = Guid.Parse("0d9f6b52-7a1c-4e43-8c2e-5b1f3a9d7e10");

    // The dataset "run-2" that a failed run of the task created: its delayed delete has not run yet
    private void SeedRunDataset()
    {
        using var db = _db.NewContext();
        var org = db.Organizations.Single(o => o.Slug == "r3photo");
        db.Datasets.Add(new Dataset
            { Slug = "run-2", Organization = org, InternalRef = RunDatasetRef, CreationDate = DateTime.UtcNow });
        db.SaveChanges();
    }

    private static Registry.Web.Services.HeavyTasks.Adapters.HeavyToolValidationContext RetryValidation(IDDB ddb,
        TaskCreatedDataset? own) =>
        new(ddb,
            new ClaimsPrincipal(new ClaimsIdentity()), NullLogger.Instance, null, own);

    [Test]
    public async Task ValidateAsync_TheRetryOfTheRunThatCreatedTheDataset_IsAccepted_AndTheDatasetStays()
    {
        SeedRunDataset();
        var tool = CreateTool(withAccessResolver: true);

        await tool.ValidateAsync(Request(new { createNewDataset = true, newDatasetName = "run-2" }),
            RetryValidation(SourceDdb().Object, new TaskCreatedDataset("r3photo", RunDatasetRef, "run-2")),
            CancellationToken.None);

        using var db = _db.NewContext();
        db.Datasets.Count(d => d.Slug == "run-2" && d.InternalRef == RunDatasetRef).ShouldBe(1);
        StoredDatasets().ShouldBe(2);
    }

    [Test]
    public void ValidateAsync_TheRetryOfAnotherTask_WhoseSlugADatasetOfAnotherTaskHas_IsRefused()
    {
        SeedRunDataset();
        var tool = CreateTool(withAccessResolver: true);

        // This task created another dataset (gone since), "run-2" is the dataset of another task
        var ex = Should.Throw<ConflictException>(async () => await tool.ValidateAsync(
            Request(new { createNewDataset = true, newDatasetName = "run-2" }),
            RetryValidation(SourceDdb().Object,
                new TaskCreatedDataset("r3photo", Guid.Parse("6c2b8e47-1d3a-4f59-b7e0-9a4c2d1e8f33"), "run-2")),
            CancellationToken.None));

        ex.Message.ShouldBe("A dataset named \"run-2\" already exists in \"r3photo\"");
        ex.StatusCode.ShouldBe(409);
    }

    [Test]
    public void ValidateAsync_TheRetryOfARunWhoseDatasetIsInAnotherOrganization_StillRefusesATakenSlug()
    {
        SeedRunDataset();
        var tool = CreateTool(withAccessResolver: true);

        var ex = Should.Throw<ConflictException>(async () => await tool.ValidateAsync(
            Request(new { createNewDataset = true, newDatasetName = "run-2" }),
            RetryValidation(SourceDdb().Object, new TaskCreatedDataset("other", RunDatasetRef, "run-2")),
            CancellationToken.None));

        ex.Message.ShouldBe("A dataset named \"run-2\" already exists in \"r3photo\"");
    }

    [Test]
    public async Task ValidateAsync_ChecksWriteAccessToTheTargetOrganizationOfTheSubmitter_AndCreatesNothing()
    {
        var tool = CreateTool(withAccessResolver: true);

        await tool.ValidateAsync(Request(new { createNewDataset = true, newDatasetName = "brighton-fast", newDatasetOrgSlug = "other" }),
            Validation(SourceDdb().Object, withCaller: true), CancellationToken.None);

        _access.Verify(a => a.ResolveOrganizationAsync("other", AccessType.Write, false, false, It.IsAny<CancellationToken>()),
            Times.Once);
        StoredDatasets().ShouldBe(1);
        _ddbManager.Verify(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public void ValidateAsync_WithoutWriteAccessToTheTargetOrganization_IsForbiddenAtSubmit()
    {
        _access.Setup(a => a.ResolveOrganizationAsync("other", AccessType.Write, false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ForbiddenException("You do not have write access to this organization"));
        var tool = CreateTool(withAccessResolver: true);

        Should.Throw<ForbiddenException>(async () => await tool.ValidateAsync(
            Request(new { createNewDataset = true, newDatasetName = "brighton-fast", newDatasetOrgSlug = "other" }),
            Validation(SourceDdb().Object, withCaller: true), CancellationToken.None));
        StoredDatasets().ShouldBe(1);
    }

    // The worker validates again with no caller, in a container that has no access resolver
    [Test]
    public async Task ValidateAsync_OnTheWorker_NeedsNoCallerAndNoAccessResolver()
    {
        var tool = CreateTool(withAccessResolver: false);

        await tool.ValidateAsync(Request(new { createNewDataset = true, newDatasetName = "phalaborwa-results" }),
            Validation(SourceDdb().Object, withCaller: false), CancellationToken.None);
    }

    // AC-007: the run ends with a new dataset that holds the results
    [Test]
    public async Task ExecuteAsync_CreatesTheNewDatasetInTheWorkerAndImportsTheResultsIntoIt()
    {
        SetUpNodeThatReturnsAResultBundle();
        var tool = CreateTool(withAccessResolver: false); // no user, no access resolver: the system path
        var recorder = new FakeTaskWriteRecorder();

        await tool.ExecuteAsync(
            Request(new { createNewDataset = true, newDatasetName = "phalaborwa-results", newDatasetVisibility = "UNLISTED" }),
            Execution(recorder).Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

        using var db = _db.NewContext();
        var created = await db.Datasets.AsNoTracking().Include(d => d.Organization)
            .SingleAsync(d => d.Slug == "phalaborwa-results");
        created.Organization.Slug.ShouldBe("r3photo");
        created.InternalRef.ShouldBe(_createdRef);

        // Its visibility and its name are written to the new dataset (UNLISTED is 1)
        _targetMeta.Verify(m => m.Set("visibility", "1"), Times.Once);
        _targetMeta.Verify(m => m.Set("name", "phalaborwa results"), Times.Once);

        // The task recorded the dataset it created (FR-XC-27) and the results are in it
        recorder.Created.ShouldBe(new Registry.Web.Services.HeavyTasks.TaskCreatedDataset("r3photo", created.InternalRef, "phalaborwa-results"));
        File.Exists(Path.Combine(_dir, "target", "odm_orthophoto", "odm_orthophoto.tif")).ShouldBeTrue();
        recorder.Paths.ShouldContain("odm_orthophoto/odm_orthophoto.tif");
    }

    [Test]
    public async Task ExecuteAsync_AnOmittedName_GetsAGeneratedSlug()
    {
        SetUpNodeThatReturnsAResultBundle();
        var tool = CreateTool(withAccessResolver: false);

        await tool.ExecuteAsync(Request(new { createNewDataset = true }), Execution(new FakeTaskWriteRecorder()).Object,
            new Progress<HeavyToolProgress>(), CancellationToken.None);

        using var db = _db.NewContext();
        db.Datasets.Select(d => d.Slug).Single(s => s != "taken").ShouldStartWith("photogrammetry-");
    }

    // D-031: a slug taken during the run fails the task with a message that names it, and nothing is created
    [Test]
    public async Task ExecuteAsync_ASlugTakenDuringTheRun_FailsNamingTheSlugAndCreatesNothing()
    {
        SetUpNodeThatReturnsAResultBundle();
        var tool = CreateTool(withAccessResolver: false);
        var recorder = new FakeTaskWriteRecorder();

        // Another user creates it while the node processes the images
        _node.Setup(n => n.GetTaskInfoAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                using var db = _db.NewContext();
                db.Datasets.Add(new Dataset
                {
                    Slug = "brighton-fast", Organization = db.Organizations.Single(o => o.Slug == "r3photo"),
                    InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
                });
                db.SaveChanges();
                return Task.FromResult(new NodeOdxTaskInfo("uuid-1", NodeOdxTaskStatusCode.Completed, null, 100, 2));
            });

        var ex = await Should.ThrowAsync<ConflictException>(() => tool.ExecuteAsync(
            Request(new { createNewDataset = true, newDatasetName = "brighton-fast" }),
            Execution(recorder).Object, new Progress<HeavyToolProgress>(), CancellationToken.None));

        ex.Message.ShouldBe("A dataset named \"brighton-fast\" already exists in \"r3photo\"");
        StoredDatasets().ShouldBe(2); // "taken" and the one of the other user: this task created none
        recorder.Created.ShouldBeNull();
        _ddbManager.Verify(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        Directory.GetFileSystemEntries(Path.Combine(_dir, "target")).ShouldBeEmpty();
    }

    // The target organization is another one than the source's: the dataset is created there
    [Test]
    public async Task ExecuteAsync_ANewDatasetOrganization_CreatesItThere()
    {
        SetUpNodeThatReturnsAResultBundle();
        var tool = CreateTool(withAccessResolver: false);
        var recorder = new FakeTaskWriteRecorder();

        await tool.ExecuteAsync(
            Request(new { createNewDataset = true, newDatasetName = "brighton-fast", newDatasetOrgSlug = "other" }),
            Execution(recorder).Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

        using var db = _db.NewContext();
        db.Datasets.Include(d => d.Organization).Single(d => d.Slug == "brighton-fast").Organization.Slug
            .ShouldBe("other");
        recorder.Created!.Org.ShouldBe("other");
    }

    // FR-PRC-08, D-039: the worker reads what the owner of the organization has left when it writes the results; a bundle
    // that does not fit stops the extraction, the task fails with the 507 text and no file of the extraction stays
    [Test]
    public async Task ExecuteAsync_ResultsThatDoNotFitTheQuotaOfTheOwner_FailWithThe507Text_AndNoFileStays()
    {
        SetUpNodeThatReturnsAResultBundle("odm_dem/dsm.tif");
        var tool = CreateTool(withAccessResolver: false);
        var storage = new Mock<Registry.Web.Services.Ports.IStorageQuota>();
        storage.Setup(q => q.OwnerIdOfAsync("r3photo", It.IsAny<CancellationToken>())).ReturnsAsync("owner-1");
        // The bundle holds 5 + 5 bytes: 7 fit the first file and not the second
        storage.Setup(q => q.RemainingBytesAsync("owner-1", It.IsAny<CancellationToken>())).ReturnsAsync(7L);

        var ex = await Should.ThrowAsync<Registry.Web.Exceptions.QuotaExceededException>(() => tool.ExecuteAsync(
            Request(new { destPath = "results" }),
            Execution(new FakeTaskWriteRecorder(), storage: storage.Object).Object, new Progress<HeavyToolProgress>(),
            CancellationToken.None));

        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 7 B left.");
        Directory.GetFiles(Path.Combine(_dir, "source"), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".uploads")).ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_AnOwnerWithNothingLeft_FailsBeforeAnythingIsWritten()
    {
        SetUpNodeThatReturnsAResultBundle();
        var tool = CreateTool(withAccessResolver: false);
        var storage = new Mock<Registry.Web.Services.Ports.IStorageQuota>();
        storage.Setup(q => q.OwnerIdOfAsync("r3photo", It.IsAny<CancellationToken>())).ReturnsAsync("owner-1");
        storage.Setup(q => q.RemainingBytesAsync("owner-1", It.IsAny<CancellationToken>())).ReturnsAsync(0L);

        var ex = await Should.ThrowAsync<Registry.Web.Exceptions.QuotaExceededException>(() => tool.ExecuteAsync(
            Request(new { destPath = "results" }),
            Execution(new FakeTaskWriteRecorder(), storage: storage.Object).Object, new Progress<HeavyToolProgress>(),
            CancellationToken.None));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 0 B left.");
    }

    // FR-XC-26: the quota that applies is the one of the owner of the organization the results go to
    [Test]
    public void StorageOrganization_IsTheOrganizationOfTheNewDataset_WhenTheResultsGoIntoOne()
    {
        var tool = CreateTool(withAccessResolver: false);

        tool.StorageOrganization(Request(new { createNewDataset = true, newDatasetName = "x-y", newDatasetOrgSlug = "other" }))
            .ShouldBe("other");
        tool.StorageOrganization(Request(new { createNewDataset = true, newDatasetName = "x-y" })).ShouldBe("r3photo");
        tool.StorageOrganization(Request(new { destPath = "results", newDatasetOrgSlug = "other" })).ShouldBe("r3photo");
    }

    // FR-PRC-41: the internal folders of the bundle are not imported (the Hub builds its own COPC), they are skipped with
    // the reason "excluded" and counted in the log
    [Test]
    public async Task ExecuteAsync_TheEptChunksOfTheBundle_AreNotImported_AndTheLogCountsThem()
    {
        SetUpNodeThatReturnsAResultBundle("entwine_pointcloud/ept-data/0-0-0-0.laz", "entwine_pointcloud/ept.json",
            "opensfm/reconstruction.json");
        var tool = CreateTool(withAccessResolver: false);
        var log = new List<string>();

        await tool.ExecuteAsync(
            Request(new { createNewDataset = true, newDatasetName = "phalaborwa-results" }),
            Execution(new FakeTaskWriteRecorder()).Object, new SyncProgress(p =>
            {
                if (p.LogChunk != null) log.AddRange(p.LogChunk.Split('\n'));
            }), CancellationToken.None);

        File.Exists(Path.Combine(_dir, "target", "odm_orthophoto", "odm_orthophoto.tif")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_dir, "target", "entwine_pointcloud")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_dir, "target", "opensfm")).ShouldBeFalse();
        log.ShouldContain("Excluded 3 internal file(s) of the archive");
        log.ShouldContain("Extraction complete: 1 extracted, 3 skipped");
        log.ShouldNotContain(l => l.StartsWith("Skipped: entwine_pointcloud"));
    }

    private sealed class SyncProgress(Action<HeavyToolProgress> report) : IProgress<HeavyToolProgress>
    {
        public void Report(HeavyToolProgress value) => report(value);
    }

    private const string NoWriteAccessText =
        "The user who started this task cannot write to the organization \"other\": the results were not imported";

    // D-031: the worker checks again, before it creates the dataset, that the user who requested the task may write to
    // the organization. Removed from it while the node processed the images, the task fails naming the organization,
    // and nothing is created
    [Test]
    public async Task ExecuteAsync_TheRequesterLosesWriteAccessDuringTheRun_FailsNamingTheOrganizationAndCreatesNothing()
    {
        SetUpNodeThatReturnsAResultBundle();
        var stillMember = true;
        _users.Setup(u => u.CanAccessOrganizationAsync(Requester, "other", AccessType.Write, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stillMember);
        _node.Setup(n => n.GetTaskInfoAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                stillMember = false; // the organization's administrator removes the requester
                return Task.FromResult(new NodeOdxTaskInfo("uuid-1", NodeOdxTaskStatusCode.Completed, null, 100, 2));
            });
        var tool = CreateTool(withAccessResolver: false);
        var recorder = new FakeTaskWriteRecorder();

        var ex = await Should.ThrowAsync<ForbiddenException>(() => tool.ExecuteAsync(
            Request(new { createNewDataset = true, newDatasetName = "brighton-fast", newDatasetOrgSlug = "other" }),
            Execution(recorder).Object, new Progress<HeavyToolProgress>(), CancellationToken.None));

        ex.Message.ShouldBe(NoWriteAccessText);
        StoredDatasets().ShouldBe(1);
        recorder.Created.ShouldBeNull();
        recorder.Paths.ShouldBeEmpty();
        _ddbManager.Verify(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        Directory.GetFileSystemEntries(Path.Combine(_dir, "target")).ShouldBeEmpty();
    }

    // The worker has no caller: the user it checks is the requester of the task (a Retry by somebody else keeps it,
    // RetryEndpointTest.Retry_ByAnotherUser_KeepsTheRequesterThatTheWorkerChecks)
    [Test]
    public async Task ExecuteAsync_ChecksTheWriteAccessOfTheRequesterOfTheTask()
    {
        SetUpNodeThatReturnsAResultBundle();
        var tool = CreateTool(withAccessResolver: false);

        await tool.ExecuteAsync(
            Request(new { createNewDataset = true, newDatasetName = "brighton-fast", newDatasetOrgSlug = "other" }),
            Execution(new FakeTaskWriteRecorder()).Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

        _users.Verify(u => u.CanAccessOrganizationAsync(Requester, "other", AccessType.Write, It.IsAny<CancellationToken>()),
            Times.Once);
        _users.Verify(u => u.CanAccessOrganizationAsync(It.Is<string>(id => id != Requester), It.IsAny<string>(),
            It.IsAny<AccessType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A task that names no requester has nobody whose access holds: it creates nothing
    [Test]
    public async Task ExecuteAsync_ATaskWithoutARequester_CreatesNothing()
    {
        SetUpNodeThatReturnsAResultBundle();
        var tool = CreateTool(withAccessResolver: false);

        var ex = await Should.ThrowAsync<ForbiddenException>(() => tool.ExecuteAsync(
            Request(new { createNewDataset = true, newDatasetName = "brighton-fast", newDatasetOrgSlug = "other" }),
            Execution(new FakeTaskWriteRecorder(), requestedBy: null).Object, new Progress<HeavyToolProgress>(),
            CancellationToken.None));

        ex.Message.ShouldBe(NoWriteAccessText);
        StoredDatasets().ShouldBe(1);
    }

    private Guid SeedDataset(string slug, string orgSlug = "r3photo")
    {
        using var db = _db.NewContext();
        var internalRef = Guid.NewGuid();
        db.Datasets.Add(new Dataset
        {
            Slug = slug, Organization = db.Organizations.Single(o => o.Slug == orgSlug), InternalRef = internalRef,
            CreationDate = DateTime.UtcNow
        });
        db.SaveChanges();
        return internalRef;
    }

    // D-037, U-114: a re-run of the same task (a Retry within the delete window, a lost run run again) finds in its
    // record the dataset it created: it imports into it again instead of finding its own slug taken
    [Test]
    public async Task ExecuteAsync_AReRunWithTheCreatedDatasetInTheRecord_ReusesIt_WithoutConflictOrSecondDataset()
    {
        SetUpNodeThatReturnsAResultBundle();
        var earlier = SeedDataset("brighton-fast");
        var recorder = new FakeTaskWriteRecorder
        {
            Created = new Registry.Web.Services.HeavyTasks.TaskCreatedDataset("r3photo", earlier, "brighton-fast")
        };
        var tool = CreateTool(withAccessResolver: false);

        await tool.ExecuteAsync(Request(new { createNewDataset = true, newDatasetName = "brighton-fast" }),
            Execution(recorder).Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

        StoredDatasets().ShouldBe(2); // "taken" and the earlier one: no second dataset
        _createdRef.ShouldBe(earlier);
        _ddbManager.Verify(m => m.Get("r3photo", earlier), Times.Once);
        _targetMeta.Verify(m => m.Set(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        recorder.CreatedCalls.ShouldBe(0);
        recorder.Created!.InternalRef.ShouldBe(earlier);
        File.Exists(Path.Combine(_dir, "target", "odm_orthophoto", "odm_orthophoto.tif")).ShouldBeTrue();
        recorder.Paths.ShouldContain("odm_orthophoto/odm_orthophoto.tif");
        // The access of the requester is checked for a reused dataset too: the task writes into it for that user
        _users.Verify(u => u.CanAccessOrganizationAsync(Requester, "r3photo", AccessType.Write, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Without a name the slug is generated again at each run: the re-run reuses the dataset of the record instead of
    // creating a second one and leaving the first behind
    [Test]
    public async Task ExecuteAsync_AnUnnamedReRun_ReusesTheRecordedDataset_AndOrphansNothing()
    {
        SetUpNodeThatReturnsAResultBundle();
        var earlier = SeedDataset("photogrammetry-20261101120000");
        var recorder = new FakeTaskWriteRecorder
        {
            Created = new Registry.Web.Services.HeavyTasks.TaskCreatedDataset("r3photo", earlier,
                "photogrammetry-20261101120000")
        };
        var tool = CreateTool(withAccessResolver: false);

        await tool.ExecuteAsync(Request(new { createNewDataset = true }), Execution(recorder).Object,
            new Progress<HeavyToolProgress>(), CancellationToken.None);

        using var db = _db.NewContext();
        db.Datasets.Select(d => d.Slug).OrderBy(s => s).ToList()
            .ShouldBe(["photogrammetry-20261101120000", "taken"]);
        _createdRef.ShouldBe(earlier);
        recorder.CreatedCalls.ShouldBe(0);
    }

    // The dataset of the record is gone (deleted after the delete window): the re-run creates its dataset again and
    // records the new one
    [Test]
    public async Task ExecuteAsync_AReRunWhoseRecordedDatasetIsGone_CreatesANewOne()
    {
        SetUpNodeThatReturnsAResultBundle();
        var gone = Guid.NewGuid();
        var recorder = new FakeTaskWriteRecorder
        {
            Created = new Registry.Web.Services.HeavyTasks.TaskCreatedDataset("r3photo", gone, "brighton-fast")
        };
        var tool = CreateTool(withAccessResolver: false);

        await tool.ExecuteAsync(Request(new { createNewDataset = true, newDatasetName = "brighton-fast" }),
            Execution(recorder).Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

        using var db = _db.NewContext();
        var created = db.Datasets.Single(d => d.Slug == "brighton-fast");
        created.InternalRef.ShouldNotBe(gone);
        recorder.CreatedCalls.ShouldBe(1);
        recorder.Created!.InternalRef.ShouldBe(created.InternalRef);
    }

    private Mock<IHeavyToolExecutionContext> Execution(FakeTaskWriteRecorder recorder, string? requestedBy = Requester,
        Registry.Web.Services.Ports.IStorageQuota? storage = null)
    {
        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(SourceDdb().Object);
        ctx.SetupGet(c => c.Storage).Returns(storage ?? Registry.Web.Services.Ports.UnlimitedStorageQuota.Instance);
        ctx.SetupGet(c => c.TaskId).Returns("task-1");
        ctx.SetupGet(c => c.RequestedBy).Returns(requestedBy);
        ctx.SetupGet(c => c.Dataset).Returns(new DatasetKey("r3photo", Guid.NewGuid()));
        ctx.SetupGet(c => c.WriteRecord).Returns(recorder);
        return ctx;
    }

    private void SetUpNodeThatReturnsAResultBundle(params string[] moreEntries)
    {
        _node.Setup(n => n.CreateTaskAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("uuid-1");
        _node.Setup(n => n.GetTaskInfoAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NodeOdxTaskInfo("uuid-1", NodeOdxTaskStatusCode.Completed, null, 100, 2));
        _node.Setup(n => n.GetTaskOutputAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _node.Setup(n => n.DownloadAssetAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", "all.zip", It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<NodeOdxEndpoint, string, string, string, CancellationToken>((_, _, _, dest, _) =>
            {
                using var fs = File.Create(dest);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                using (var entry = zip.CreateEntry("odm_orthophoto/odm_orthophoto.tif").Open())
                    entry.Write("ortho"u8);
                foreach (var more in moreEntries)
                {
                    using var extra = zip.CreateEntry(more).Open();
                    extra.Write("chunk"u8);
                }

                return Task.CompletedTask;
            });
    }
}
