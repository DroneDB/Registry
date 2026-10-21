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

    [Test]
    public async Task CheckAndQueueAsync_EmptyDatabase_DoesNothing()
    {
        await using var context = GetEmptyContext();

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

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
        ddbMock.Setup(x => x.IsBuildable("readme.txt")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Never);
        ddbMock.Verify(x => x.IsBuildComplete(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildActive_Skipped()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new DdbEntry { Path = "ortho.tif", Hash = "h2" }]);
        ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("ortho.tif")).Returns(true);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

        await service.CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(
            x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()),
            Times.Never);
        ddbMock.Verify(x => x.IsBuildActive("ortho.tif"), Times.Once);
    }

    [Test]
    public async Task CheckAndQueueAsync_BatchStatus_AsksActivityOnlyForIncompleteBuildableEntries()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([
            new DdbEntry { Path = "readme.txt", Hash = "h1" },
            new DdbEntry { Path = "ortho.tif", Hash = "h2" },
            new DdbEntry { Path = "layer.fgb", Hash = "h3" },
            new DdbEntry { Path = "pc.laz", Hash = "h4" }
        ]);
        ddbMock.Setup(x => x.GetBuildStatus(It.IsAny<string[]>())).Returns((string[] paths) => paths
            .Select(p => new EntryBuildStatus
            {
                Path = p, Exists = true, Buildable = p != "readme.txt", Complete = p == "ortho.tif"
            }).ToArray());
        ddbMock.Setup(x => x.IsBuildActive("pc.laz")).Returns(true);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        await new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object,
            _logger).CheckAndQueueAsync(null, CancellationToken.None);

        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.Is<IndexPayload>(p => p.Path == "layer.fgb" && p.Hash == "h3")), Times.Once);
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.IsAny<IndexPayload>()), Times.Once);
        ddbMock.Verify(x => x.GetBuildStatus(It.IsAny<string[]>()), Times.Once);
        ddbMock.Verify(x => x.IsBuildable(It.IsAny<string>()), Times.Never);
        ddbMock.Verify(x => x.IsBuildComplete(It.IsAny<string>()), Times.Never);
        ddbMock.Verify(x => x.IsBuildActive(It.Is<string>(p => p != "layer.fgb" && p != "pc.laz")), Times.Never);
    }

    [Test]
    public async Task CheckAndQueueAsync_BuildComplete_Skipped()
    {
        await using var context = GetTest1Context();

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>()))
            .Returns([new DdbEntry { Path = "ortho.tif", Hash = "h2" }]);
        ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("ortho.tif")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("ortho.tif")).Returns(true);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

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
        ddbMock.Setup(x => x.IsBuildable("layer.geojson")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("layer.geojson")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("layer.geojson")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

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
        ddbMock.Setup(x => x.IsBuildable("readme.txt")).Returns(false);
        ddbMock.Setup(x => x.IsBuildable("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildable("pc.laz")).Returns(true);
        ddbMock.Setup(x => x.IsBuildable("layer.fgb")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("ortho.tif")).Returns(false);
        ddbMock.Setup(x => x.IsBuildActive("pc.laz")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("layer.fgb")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("ortho.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildComplete("layer.fgb")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

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

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

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
        ddbMock.Setup(x => x.IsBuildable("broken.tif"))
            .Throws(new InvalidOperationException("boom"));
        ddbMock.Setup(x => x.IsBuildable("good.tif")).Returns(true);
        ddbMock.Setup(x => x.IsBuildActive("good.tif")).Returns(false);
        ddbMock.Setup(x => x.IsBuildComplete("good.tif")).Returns(false);

        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var service = new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger);

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
        ddb.Setup(x => x.IsBuildable(It.IsAny<string>())).Returns(true);
        // Native DDBIsBuildComplete is read-only and reports only the identifiable
        // interrupted tiles swap as incomplete, while ordinary Nexus output is complete.
        ddb.Setup(x => x.IsBuildComplete("interrupted.obj")).Returns(false);
        ddb.Setup(x => x.IsBuildComplete("nexus-only.obj")).Returns(true);
        _ddbManagerMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);
        await new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger)
            .CheckAndQueueAsync(null, CancellationToken.None);
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.Is<IndexPayload>(meta => meta.Path == "interrupted.obj" && meta.Hash == "swap-hash")), Times.Once);
        _backgroundJobMock.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(),
            It.IsAny<IndexPayload>()), Times.Once);
    }

    // ---- DDB-12: pending entries and repeated failures are not re-enqueued every night ----

    private Mock<IDDB> SingleIncompleteEntry(string path, string hash, PendingBuildInfo[] pending = null)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([new DdbEntry { Path = path, Hash = hash }]);
        ddb.Setup(x => x.IsBuildable(path)).Returns(true);
        ddb.Setup(x => x.IsBuildActive(path)).Returns(false);
        ddb.Setup(x => x.IsBuildComplete(path)).Returns(false);
        ddb.Setup(x => x.GetPendingBuildInfo()).Returns(pending ?? []);
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
        SingleIncompleteEntry("site.dwg", "h1",
            [new PendingBuildInfo { Path = "site.dwg", Hash = "h1", MissingDependencies = ["site.prj"] }]);

        await new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger)
            .CheckAndQueueAsync(null, CancellationToken.None);

        EnqueuedCount().ShouldBe(0);
    }

    [Test]
    public async Task CheckAndQueueAsync_FailedLastNight_WaitsBeforeRetrying()
    {
        await using var context = GetTest1Context();
        SingleIncompleteEntry("bad.dwg", "h1");
        AddBuildJob(context, "bad.dwg", "h1", "Failed", DateTime.UtcNow.AddHours(-2), "InvalidOperationException");

        await new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger)
            .CheckAndQueueAsync(null, CancellationToken.None);

        EnqueuedCount().ShouldBe(0);
    }

    [Test]
    public async Task CheckAndQueueAsync_NewContentAfterFailures_RetriedAtOnce()
    {
        await using var context = GetTest1Context();
        SingleIncompleteEntry("bad.dwg", "h2"); // re-uploaded: new hash
        AddBuildJob(context, "bad.dwg", "h1", "Failed", DateTime.UtcNow.AddHours(-2), "InvalidOperationException");

        await new ArtifactCompletenessCheckerService(context, new DatasetSweep(context), _ddbManagerMock.Object, _backgroundJobMock.Object, _logger)
            .CheckAndQueueAsync(null, CancellationToken.None);

        EnqueuedCount().ShouldBe(1);
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
        // XI-02: a full disk or a tool timeout (libddb ABI 1.1 reason) is transient, not a content failure
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
