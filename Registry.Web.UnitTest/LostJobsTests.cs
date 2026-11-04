using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Hosting;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The jobs the InMemory storage loses at a restart: its job ids are GUIDs, so a new job never takes the id of
/// an old task; the startup sweep ends the tasks the earlier process left queued or running; Cancel ends a task
/// whose job is gone instead of answering "not deleted" and leaving it running for ever.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LostJobsSweepTests
{
    private PublicationEnv _env = null!;
    private static readonly DateTime Before = new(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private static readonly DateTime Started = Before.AddHours(1);
    private static readonly string[] Concluded = ["1", "2", "3"];

    [SetUp]
    public void SetUp() => _env = new PublicationEnv();

    [TearDown]
    public void TearDown() => _env.Dispose();

    private JobIndexSyncService Service(JobStorage storage) =>
        new(new JobIndexQuery(_env.Db), _env.Writer, storage, NullLogger<JobIndexSyncService>.Instance);

    private static InMemoryStorage GuidStorage() => new(RegistryJobsServiceCollectionExtensions.InMemoryStorageOptions);

    [TestCase("Created")]
    [TestCase("Indexing")]
    [TestCase("Enqueued")]
    [TestCase("Scheduled")]
    [TestCase("Processing")]
    public async Task ActiveRowOfTheEarlierProcess_IsDeleted(string state)
    {
        _env.SeedRow("34", Before, state);

        (await Service(GuidStorage()).CloseLostAsync(Started)).ShouldBe(1);

        var row = _env.Row("34")!;
        row.CurrentState.ShouldBe("Deleted");
        row.DeletedAtUtc.ShouldNotBeNull();
    }

    [Test]
    public async Task ActiveRowOfANumberedStorage_IsDeleted()
    {
        _env.SeedRow("34", Before, "Processing");

        (await Service(new InMemoryStorage()).CloseLostAsync(Started)).ShouldBe(1);

        _env.Row("34")!.CurrentState.ShouldBe("Deleted");
    }

    [Test]
    public async Task ConcludedRows_AreUntouched()
    {
        _env.SeedRow("1", Before, "Succeeded");
        _env.SeedRow("2", Before, "Failed");
        _env.SeedRow("3", Before, "Deleted");
        var before = Concluded.Select(id => PublicationEnv.Snapshot(_env.Row(id))).ToArray();

        (await Service(GuidStorage()).CloseLostAsync(Started)).ShouldBe(0);

        Concluded.Select(id => PublicationEnv.Snapshot(_env.Row(id))).ShouldBe(before);
    }

    [Test]
    public async Task RowChangedAfterTheStart_IsUntouched()
    {
        // The state filter writes the row before Hangfire commits the job: a job of this process may not be visible yet
        _env.SeedRow("7", Started, "Enqueued");
        var before = PublicationEnv.Snapshot(_env.Row("7"));

        (await Service(GuidStorage()).CloseLostAsync(Started)).ShouldBe(0);

        PublicationEnv.Snapshot(_env.Row("7")).ShouldBe(before);
    }

    [Test]
    public async Task RowWhoseJobIsInTheStorage_IsUntouched()
    {
        var storage = GuidStorage();
        var id = new BackgroundJobClient(storage).Create(PublicationEnv.NewJob(), new EnqueuedState());
        _env.SeedRow(id, Before, "Enqueued");

        (await Service(storage).CloseLostAsync(Started)).ShouldBe(0);

        _env.Row(id)!.CurrentState.ShouldBe("Enqueued");
    }

    [Test]
    public async Task MoreRowsThanAPage_AreAllEnded_AndTheLiveOnesKept()
    {
        var storage = GuidStorage();
        var client = new BackgroundJobClient(storage);
        var live = Enumerable.Range(0, 3).Select(_ => client.Create(PublicationEnv.NewJob(), new EnqueuedState())).ToArray();
        for (var i = 0; i < 7; i++)
        {
            // Lost and live rows interleaved in the order of the query (creation time)
            _env.SeedRow($"lost{i}", Before.AddMinutes(2 * i), "Enqueued");
            if (i < live.Length)
                _env.SeedRow(live[i], Before.AddMinutes(2 * i + 1), "Enqueued");
        }

        (await Service(storage).CloseLostAsync(Started, pageSize: 2)).ShouldBe(7);

        Enumerable.Range(0, 7).ShouldAllBe(i => _env.Row($"lost{i}")!.CurrentState == "Deleted");
        live.ShouldAllBe(id => _env.Row(id)!.CurrentState == "Enqueued");
    }

    [Test]
    public void InMemoryStorage_NumbersItsJobsWithGuids_SoARestartReusesNoId()
    {
        RegistryJobsServiceCollectionExtensions.InMemoryStorageOptions.IdType.ShouldBe(InMemoryStorageIdType.Guid);

        var first = new BackgroundJobClient(GuidStorage()).Create(PublicationEnv.NewJob(), new EnqueuedState());
        var afterRestart = new BackgroundJobClient(GuidStorage()).Create(PublicationEnv.NewJob(), new EnqueuedState());

        Guid.TryParse(first, out _).ShouldBeTrue();
        afterRestart.ShouldNotBe(first);
    }
}

/// <summary>
/// DELETE /orgs/{o}/ds/{d}/tasks/{id}: a queued or running task is cancelled (its job deleted, or the task ended
/// when the job store lost its job), a concluded one is a bad request.
/// </summary>
[TestFixture]
public class CancelTaskTests : IDisposable
{
    private const string Org = "org1";
    private const string Ds = "ds1";

    private RegistryContext _db = null!;

    [SetUp]
    public void SetUp() => _db = TestDb.CreateContext<RegistryContext>();

    [TearDown]
    public void Dispose()
    {
        _db?.Dispose();
        GC.SuppressFinalize(this);
    }

    private TaskLifecycle Lifecycle(JobStorage storage)
    {
        var authManager = new Mock<IAuthManager>();
        authManager.Setup(x => x.RequestAccess<Dataset>(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        authManager.Setup(x => x.GetCurrentUser()).ReturnsAsync((User)null!);
        authManager.Setup(x => x.IsOwnerOrAdmin(It.IsAny<Dataset>())).ReturnsAsync(true);

        var utils = new Mock<IDatasetResolver>();
        utils.Setup(x => x.GetDatasetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() });
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(new Mock<IDDB>().Object);

        var access = new DatasetAccessResolver(utils.Object, authManager.Object, ddbManager.Object, TestPaths.Policy);
        var authorizer = new TaskAuthorizer(access, new JobIndexQuery(_db), authManager.Object);
        var processor = new BackgroundJobsProcessor(new BackgroundJobClient(storage), NullIndexedEnqueuer.Create(), storage);
        var writer = new JobIndexWriter(_db, NullLogger<JobIndexWriter>.Instance, new Mock<IRecordCache>().Object);
        var artifacts = new TaskArtifactStore(new Registry.Adapters.FileSystem(), TestScratch.Provider,
            NullLogger<TaskArtifactStore>.Instance);
        return new TaskLifecycle(access, authorizer, processor, writer, artifacts, NullLogger<TaskLifecycle>.Instance);
    }

    private void Seed(string jobId, string state)
    {
        _db.JobIndices.Add(new JobIndex
        {
            JobId = jobId, OrgSlug = Org, DsSlug = Ds, ToolId = "build", ToolVersion = "1", CurrentState = state,
            CreatedAtUtc = DateTime.UtcNow, LastStateChangeUtc = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private JobIndex Row(string jobId)
    {
        _db.ChangeTracker.Clear();
        return _db.JobIndices.Single(x => x.JobId == jobId);
    }

    private static InMemoryStorage GuidStorage() => new(RegistryJobsServiceCollectionExtensions.InMemoryStorageOptions);

    [Test]
    public async Task QueuedTaskWithItsJob_DeletesTheJob()
    {
        var storage = GuidStorage();
        var id = new BackgroundJobClient(storage).Create(PublicationEnv.NewJob(), new EnqueuedState());
        Seed(id, "Enqueued");

        (await Lifecycle(storage).CancelAsync(Org, Ds, id)).ShouldBeTrue();

        using var connection = storage.GetConnection();
        connection.GetStateData(id)!.Name.ShouldBe(DeletedState.StateName);
    }

    [TestCase("Enqueued")]
    [TestCase("Processing")]
    public async Task TaskWhoseJobWasLost_Ends(string state)
    {
        Seed("34", state);   // a number of the earlier numbering, in a GUID store that has no such job

        (await Lifecycle(GuidStorage()).CancelAsync(Org, Ds, "34")).ShouldBeTrue();

        var row = Row("34");
        row.CurrentState.ShouldBe("Deleted");
        row.DeletedAtUtc.ShouldNotBeNull();
    }

    [Test]
    public async Task TaskWhoseJobWasLost_EndsWithANumberedStoreToo()
    {
        Seed("34", "Enqueued");

        (await Lifecycle(new InMemoryStorage()).CancelAsync(Org, Ds, "34")).ShouldBeTrue();

        Row("34").CurrentState.ShouldBe("Deleted");
    }

    [TestCase("Succeeded")]
    [TestCase("Failed")]
    [TestCase("Deleted")]
    public async Task ConcludedTask_IsABadRequest_AndStaysAsItIs(string state)
    {
        Seed("9", state);

        var ex = await Should.ThrowAsync<BadRequestException>(() => Lifecycle(GuidStorage()).CancelAsync(Org, Ds, "9"));

        ex.Message.ShouldContain("already ended");
        Row("9").CurrentState.ShouldBe(state);
    }
}
