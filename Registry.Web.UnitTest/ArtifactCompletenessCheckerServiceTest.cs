using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Shouldly;
using DdbEntry = Registry.Ports.DroneDB.Entry;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
public class ArtifactCompletenessCheckerServiceTest : TestBase
{
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock;
    private ILogger<ArtifactCompletenessCheckerService> _logger;

    [SetUp]
    public void Setup()
    {
        _ddbManagerMock = new Mock<IDdbManager>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _logger = CreateTestLogger<ArtifactCompletenessCheckerService>();
    }

    private ArtifactCompletenessCheckerService Service(RegistryContext context, IJobIndexQuery jobIndex = null) =>
        new(context, new DatasetSweep(context), _ddbManagerMock.Object, TestJobs.Scheduler(_backgroundJobMock.Object),
            jobIndex ?? new JobIndexQuery(context), _logger);

    [Test]
    public async Task CheckAndQueueAsync_EmptyDatabase_DoesNothing()
    {
        await using var context = GetEmptyContext();

        var service = Service(context);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_NonBuildableEntries_NoRebuildEnqueued()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new DdbEntry { Path = "readme.txt", Hash = "h1" }]);
        ddbMock.SetupBuilds(TestBuilds.NotBuildable("readme.txt"));

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = Service(context);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildActive_Skipped()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new DdbEntry { Path = "ortho.tif", Hash = "h2" }]);
        ddbMock.SetupBuilds(Build("ortho.tif", BuildState.Building));

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = Service(context);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_OneManifestCallAnswersEveryEntry()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([
            new DdbEntry { Path = "readme.txt", Hash = "h1" },
            new DdbEntry { Path = "ortho.tif", Hash = "h2" },
            new DdbEntry { Path = "layer.fgb", Hash = "h3" },
            new DdbEntry { Path = "pc.laz", Hash = "h4" }
        ]);
        ddbMock.SetupBuilds(TestBuilds.NotBuildable("readme.txt"), Build("ortho.tif", BuildState.Complete),
            Build("layer.fgb", BuildState.None), Build("pc.laz", BuildState.Building));

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        await Service(context).CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.Is<IndexPayload>(p => p.Path == "layer.fgb" && p.Hash == "h3")), Times.Once);
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.IsAny<IndexPayload>()), Times.Once);
        ddbMock.Verify(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildComplete_Skipped()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new DdbEntry { Path = "ortho.tif", Hash = "h2" }]);
        ddbMock.SetupBuilds(Build("ortho.tif", BuildState.Complete));

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = Service(context);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildIncomplete_EnqueuesRebuild()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new DdbEntry { Path = "layer.geojson", Hash = "h3" }]);
        ddbMock.SetupBuilds(Build("layer.geojson", BuildState.None));

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = Service(context);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<Expression<Action<DdbJobs>>>(),
                It.Is<IndexPayload>(p =>
                    p.Path == "layer.geojson" &&
                    p.Hash == "h3" &&
                    p.UserId == MagicStrings.AutoBuildServiceUserId)),
            Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_MixedEntries_OnlyIncompleteEnqueued()
    {
        await using var context = GetTest1Context();

        var entries = new[]
        {
            new DdbEntry { Path = "readme.txt", Hash = "h1" },          // not buildable
            new DdbEntry { Path = "ortho.tif", Hash = "h2" },           // complete
            new DdbEntry { Path = "pc.laz",     Hash = "h3" },          // active
            new DdbEntry { Path = "layer.fgb",  Hash = "h4" },          // INCOMPLETE
        };
        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns(entries);
        ddbMock.SetupBuilds(TestBuilds.NotBuildable("readme.txt"), Build("ortho.tif", BuildState.Complete),
            Build("pc.laz", BuildState.Building), Build("layer.fgb", BuildState.None));

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = Service(context);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Once);
        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<Expression<Action<DdbJobs>>>(),
                It.Is<IndexPayload>(p => p.Path == "layer.fgb")),
            Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_DdbThrows_ContinuesWithOtherDatasets()
    {
        await using var context = GetTest1Context();

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Throws(new InvalidOperationException("boom"));

        var service = Service(context);

        // Should not throw, just log
        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_EntryEvaluationThrows_SkipsButContinues()
    {
        await using var context = GetTest1Context();

        var entries = new[]
        {
            new DdbEntry { Path = "broken.tif", Hash = "h1" },
            new DdbEntry { Path = "good.tif",   Hash = "h2" },
        };
        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns(entries);
        // The batch fails on the broken entry, then the entry-by-entry fallback isolates it
        ddbMock.Setup(x => x.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()))
            .Returns((IReadOnlyList<string> paths) => paths.Contains("broken.tif")
                ? throw new InvalidOperationException("boom")
                : paths.Select(p => Build(p, BuildState.None)).ToArray());

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = Service(context);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(
                It.IsAny<Expression<Action<DdbJobs>>>(),
                It.Is<IndexPayload>(p => p.Path == "good.tif")),
            Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_ModelSwapRecoveryQueuedAndOrdinaryNexusOnlyModelSkipped()
    {
        await using var context = GetTest1Context();
        var ddb = new Mock<IDDB>();
        ddb.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([
            new DdbEntry { Path = "interrupted.obj", Hash = "swap-hash" },
            new DdbEntry { Path = "nexus-only.obj", Hash = "ordinary-hash" }
        ]);
        // libddb's manifest is read-only and reports only the identifiable interrupted tiles swap
        // as incomplete, while ordinary Nexus output is complete
        ddb.SetupBuilds(Build("interrupted.obj", BuildState.None), Build("nexus-only.obj", BuildState.Complete));
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);
        await Service(context)
            .CheckAndQueueAsync(null, CancellationToken.None);
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.Is<IndexPayload>(meta => meta.Path == "interrupted.obj" && meta.Hash == "swap-hash")), Times.Once);
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.IsAny<IndexPayload>()), Times.Once);
    }

    // ---- pending entries and repeated failures are not re-enqueued every night ----

    private const string Folder = "/datasets/ds";

    private static EntryBuildArtifacts Build(string path, BuildState state) =>
        TestBuilds.Build(Folder, path, EntryType.GeoRaster, "h-" + path, state);

    private Mock<IDDB> SingleIncompleteEntry(string path, string hash, string[] missingDependencies = null)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([new DdbEntry { Path = path, Hash = hash }]);
        ddb.SetupBuilds(missingDependencies == null
            ? Build(path, BuildState.None)
            : TestBuilds.Pending(Folder, path, EntryType.Vector, hash, missingDependencies));
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);
        return ddb;
    }

    private static void AddBuildJob(RegistryContext context, string path, string hash, string state,
        DateTime at, string errorType = null)
    {
        var ds = context.Datasets.Include(d => d.Organization).First();
        context.JobIndices.Add(new JobIndex
        {
            JobId = Guid.NewGuid().ToString("N"),
            OrgSlug = ds.Organization.Slug,
            DsSlug = ds.Slug,
            Path = path,
            Hash = hash,
            ToolId = "build",
            CurrentState = state,
            ErrorType = errorType,
            CreatedAtUtc = at,
            FailedAtUtc = state == "Failed" ? at : null,
            LastStateChangeUtc = at
        });
        context.SaveChanges();
    }

    private int EnqueuedCount() => _backgroundJobMock.Invocations.Count(i => i.Method.Name == "EnqueueIndexed");

    [Test]
    public async Task CheckAndQueueAsync_PendingBuild_NotReenqueued()
    {
        await using var context = GetTest1Context();
        SingleIncompleteEntry("site.dwg", "h1", ["site.prj"]);

        await Service(context)
            .CheckAndQueueAsync(null, CancellationToken.None);

        EnqueuedCount().ShouldBe(0);
    }

    [Test]
    public async Task CheckAndQueueAsync_FailedLastNight_WaitsBeforeRetrying()
    {
        await using var context = GetTest1Context();
        SingleIncompleteEntry("bad.dwg", "h1");
        AddBuildJob(context, "bad.dwg", "h1", "Failed", DateTime.UtcNow.AddHours(-2), "InvalidOperationException");

        await Service(context)
            .CheckAndQueueAsync(null, CancellationToken.None);

        EnqueuedCount().ShouldBe(0);
    }

    [Test]
    public async Task CheckAndQueueAsync_NewContentAfterFailures_RetriedAtOnce()
    {
        await using var context = GetTest1Context();
        SingleIncompleteEntry("bad.dwg", "h2"); // re-uploaded: new hash
        AddBuildJob(context, "bad.dwg", "h1", "Failed", DateTime.UtcNow.AddHours(-2), "InvalidOperationException");

        await Service(context)
            .CheckAndQueueAsync(null, CancellationToken.None);

        EnqueuedCount().ShouldBe(1);
    }

    // ---- background rebuilds: an entry of an older layout is not queued in front of the builds users wait for ----

    /// <summary>The job method and entry path of each build job the sweep queued, in order.</summary>
    private List<(string Method, string Path)> QueuedJobs() => _backgroundJobMock.Invocations
        .Where(i => i.Method.Name == "EnqueueIndexed")
        .Select(i => (Method: ((MethodCallExpression)((LambdaExpression)i.Arguments[0]).Body).Method.Name,
            Path: ((IndexPayload)i.Arguments[1]).Path))
        .ToList();

    private static EntryBuildArtifacts LegacyBuild(string path) =>
        TestBuilds.Build(Folder, path, EntryType.Model, "h-" + path, BuildState.None,
            TestBuilds.File(ArtifactRoles.ModelNexus, "nxs/model.nxz", legacy: true),
            TestBuilds.Directory(ArtifactRoles.Model3DTiles, "3dtiles", "tileset.json"));

    private void Dataset(params EntryBuildArtifacts[] builds)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns(builds.Select(b => new DdbEntry { Path = b.Path, Hash = b.Hash }).ToArray());
        ddb.SetupBuilds(builds);
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);
    }

    [Test]
    public async Task CheckAndQueueAsync_IncompleteEntryListingALegacyArtifact_IsRebuiltInTheBackground()
    {
        await using var context = GetTest1Context();
        Dataset(LegacyBuild("old.obj"), Build("layer.fgb", BuildState.None), Build("ortho.tif", BuildState.Complete));

        await Service(context).CheckAndQueueAsync(null, CancellationToken.None);

        QueuedJobs().ShouldBe([(nameof(DdbJobs.RebuildInBackground), "old.obj"), (nameof(DdbJobs.Build), "layer.fgb")],
            ignoreOrder: true);
        // The same bookkeeping as any queued build: the sweep is the user, the hash names the content
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.Is<IndexPayload>(p => p.Path == "old.obj" && p.Hash == "h-old.obj" &&
                                     p.UserId == MagicStrings.AutoBuildServiceUserId && p.ToolId == "build")), Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_CompleteEntryListingALegacyArtifact_IsLeftAlone()
    {
        await using var context = GetTest1Context();
        var complete = LegacyBuild("old.obj");
        complete.Build!.State = BuildState.Complete;
        Dataset(complete);

        await Service(context).CheckAndQueueAsync(null, CancellationToken.None);

        QueuedJobs().ShouldBeEmpty();
    }

    [TestCase("Enqueued")]
    [TestCase("Scheduled")]
    [TestCase("Processing")]
    public async Task CheckAndQueueAsync_EntryWithAnActiveBuildJob_GetsNoSecondJob(string state)
    {
        await using var context = GetTest1Context();
        Dataset(LegacyBuild("old.obj"), Build("layer.fgb", BuildState.None), Build("other.tif", BuildState.None));
        AddBuildJob(context, "old.obj", "h-old.obj", state, DateTime.UtcNow.AddMinutes(-5));
        AddBuildJob(context, "layer.fgb", "h-layer.fgb", state, DateTime.UtcNow.AddMinutes(-5));
        // A finished job says nothing about the next one
        AddBuildJob(context, "other.tif", "h-other.tif", "Succeeded", DateTime.UtcNow.AddMinutes(-5));

        await Service(context).CheckAndQueueAsync(null, CancellationToken.None);

        QueuedJobs().ShouldBe([(nameof(DdbJobs.Build), "other.tif")]);
    }

    [Test]
    public async Task CheckAndQueueAsync_ActiveJobsAreReadOnceForTheDataset()
    {
        await using var context = GetTest1Context();
        Dataset(LegacyBuild("a.obj"), LegacyBuild("b.obj"), Build("c.tif", BuildState.None));
        var jobIndex = new Mock<IJobIndexQuery>(MockBehavior.Strict);
        jobIndex.Setup(x => x.GetActivePathsAsync("public", "default", "build",
                It.Is<IReadOnlyCollection<string>>(p => p.Count == 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["b.obj"]);

        await Service(context, jobIndex.Object).CheckAndQueueAsync(null, CancellationToken.None);

        QueuedJobs().ShouldBe([(nameof(DdbJobs.RebuildInBackground), "a.obj"), (nameof(DdbJobs.Build), "c.tif")],
            ignoreOrder: true);
        jobIndex.VerifyAll();
    }

    [Test]
    public async Task CheckAndQueueAsync_FailureBackoffAppliesToABackgroundRebuild()
    {
        await using var context = GetTest1Context();
        Dataset(LegacyBuild("old.obj"));
        AddBuildJob(context, "old.obj", "h-old.obj", "Failed", DateTime.UtcNow.AddHours(-2), "InvalidOperationException");

        await Service(context).CheckAndQueueAsync(null, CancellationToken.None);

        QueuedJobs().ShouldBeEmpty();
    }

    [Test]
    public void ShouldBackOff_DoublesPerConsecutiveFailureUpTo30Days()
    {
        var now = new DateTime(2026, 10, 19, 2, 30, 0, DateTimeKind.Utc);
        ArtifactCompletenessCheckerService.BuildAttempt Failed(double daysAgo, string hash = "h", string type = "InvalidOperationException") =>
            new(hash, "Failed", type, now.AddDays(-daysAgo));

        // 1 failure: retry the next night.
        ArtifactCompletenessCheckerService.ShouldBackOff([Failed(0.1)], "h", now, out _, out _).ShouldBeTrue();
        ArtifactCompletenessCheckerService.ShouldBackOff([Failed(1)], "h", now, out _, out _).ShouldBeFalse();

        // 3 consecutive failures: wait 4 days after the latest.
        var three = new[] { Failed(3), Failed(4), Failed(5) };
        ArtifactCompletenessCheckerService.ShouldBackOff(three, "h", now, out var n, out _).ShouldBeTrue();
        n.ShouldBe(3);
        ArtifactCompletenessCheckerService.ShouldBackOff([Failed(4), Failed(5), Failed(6)], "h", now, out _, out _).ShouldBeFalse();

        // Many failures: capped at 30 days.
        var many = Enumerable.Range(0, 10).Select(i => Failed(29 + i)).ToArray();
        ArtifactCompletenessCheckerService.ShouldBackOff(many, "h", now, out _, out _).ShouldBeTrue();
        many = Enumerable.Range(0, 10).Select(i => Failed(30 + i)).ToArray();
        ArtifactCompletenessCheckerService.ShouldBackOff(many, "h", now, out _, out _).ShouldBeFalse();

        // A later success resets the count; transient failures and other hashes do not count.
        ArtifactCompletenessCheckerService.ShouldBackOff(
            [new("h", "Succeeded", null, now.AddDays(-0.5)), Failed(1), Failed(2)], "h", now, out _, out _).ShouldBeFalse();
        ArtifactCompletenessCheckerService.ShouldBackOff(
            [Failed(0.1, type: "DdbBusyException")], "h", now, out _, out _).ShouldBeFalse();
        // A full disk or a tool timeout (libddb ABI 2.0 reason) is transient, not a content failure
        ArtifactCompletenessCheckerService.ShouldBackOff(
            [Failed(0.1, type: nameof(DdbRetryableException)), Failed(0.2, type: nameof(DdbRetryableException))],
            "h", now, out _, out _).ShouldBeFalse();
        ArtifactCompletenessCheckerService.ShouldBackOff([Failed(0.1, hash: "old")], "h", now, out _, out _).ShouldBeFalse();
    }

    private static RegistryContext GetEmptyContext()
    {
        return TestDb.CreateContext<RegistryContext>();
    }

    private static RegistryContext GetTest1Context()
    {
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            var org = new Organization
            {
                Slug = MagicStrings.PublicOrganizationSlug,
                Name = "Public",
                CreationDate = DateTime.Now,
                Description = "Public organization",
                IsPublic = true,
                OwnerId = null
            };
            var ds = new Dataset
            {
                Slug = MagicStrings.DefaultDatasetSlug,
                CreationDate = DateTime.Now,
                InternalRef = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75")
            };
            org.Datasets = new List<Dataset> { ds };
            context.Organizations.Add(org);
            context.SaveChanges();
        });
    }
}
