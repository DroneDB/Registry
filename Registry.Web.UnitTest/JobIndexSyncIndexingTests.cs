using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.States;
using Moq;
using NUnit.Framework;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// JobIndex state synchronization of Indexing rows (orphans after an InMemory restart, live jobs,
/// concurrent writers) against the configured Hangfire storage.
/// </summary>
[TestFixture]
[NonParallelizable]
public class JobIndexSyncIndexingTests
{
    private PublicationEnv _env = null!;
    private static readonly DateTime Created = new(2026, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);

    [SetUp]
    public void SetUp() => _env = new PublicationEnv();

    [TearDown]
    public void TearDown() => _env.Dispose();

    private Task<JobIndexSyncService.SyncResult> Sync(JobStorage storage, IJobIndexQuery? query = null, IJobIndexWriter? writer = null) =>
        new JobIndexSyncService(query ?? new JobIndexQuery(_env.Db), writer ?? _env.Writer, storage,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JobIndexSyncService>.Instance).SyncAsync();

    [Test]
    public async Task OrphanedIndexingRow_AfterInMemoryRestart_BecomesDeleted()
    {
        var old = new InMemoryStorage();
        var oldClient = new BackgroundJobClient(old);
        var record = _env.Immediate(TimeSpan.FromMinutes(5));
        var id = oldClient.Create(PublicationEnv.NewJob(), new IndexingState(record));
        _env.SeedRow(id, record.CreatedAtUtc, "Indexing", record.Payload);
        var restarted = new InMemoryStorage();   // the process restarted: no jobs survive

        await Sync(restarted);

        var row = _env.Row(id)!;
        row.CurrentState.ShouldBe("Deleted");
        row.DeletedAtUtc.ShouldNotBeNull();
    }

    [Test]
    public async Task OrphanedCreatedRow_StillBecomesDeleted()
    {
        _env.SeedRow("77", Created, "Created");
        await Sync(new InMemoryStorage());
        _env.Row("77")!.CurrentState.ShouldBe("Deleted");
    }

    [Test]
    public async Task IndexingRowWithLiveIndexingJob_IsUntouched()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(5));
        var id = _env.CreateIndexing(record);
        _env.SeedRow(id, record.CreatedAtUtc, "Indexing", record.Payload);
        var before = PublicationEnv.Snapshot(_env.Row(id));

        await Sync(_env.Storage);

        PublicationEnv.Snapshot(_env.Row(id)).ShouldBe(before);
        _env.State(id).ShouldBe("Indexing");
    }

    [Test]
    public async Task IndexingRowWhoseJobAdvanced_FollowsHangfireState()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(5));
        var id = _env.CreateIndexing(record);
        _env.SeedRow(id, record.CreatedAtUtc, "Indexing", record.Payload);
        _env.Client.ChangeState(id, new EnqueuedState("critical"), "Indexing").ShouldBeTrue();

        await Sync(_env.Storage);

        _env.Row(id)!.CurrentState.ShouldBe("Enqueued");
    }

    [Test]
    public async Task RowAdvancedConcurrently_IsNotOverwrittenByTheSync()
    {
        // The query returns a stale snapshot (Indexing) while the database row already moved to
        // Processing with telemetry; the job is missing from Hangfire, which would mean "Deleted".
        _env.SeedRow("5", Created, "Processing");
        var before = PublicationEnv.Snapshot(_env.Row("5"));
        var stale = new JobIndex { JobId = "5", OrgSlug = "org", DsSlug = "ds", ToolId = "build", CurrentState = "Indexing" };
        var query = new Mock<IJobIndexQuery>();
        query.Setup(q => q.GetByStateAsync("Indexing", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([stale]);
        query.Setup(q => q.GetByStateAsync("Created", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await Sync(new InMemoryStorage(), query.Object);

        PublicationEnv.Snapshot(_env.Row("5")).ShouldBe(before);
    }

    [Test]
    public async Task UsesThePassedStorage_NotJobStorageCurrent()
    {
        var other = new InMemoryStorage();
        using var scope = new JobStorageScope(other);
        var record = _env.Immediate(TimeSpan.FromMinutes(5));
        var id = _env.CreateIndexing(record);                       // lives only in _env.Storage
        _env.SeedRow(id, record.CreatedAtUtc, "Indexing", record.Payload);

        await Sync(_env.Storage);                                   // Current is empty: would delete the row
        _env.Row(id)!.CurrentState.ShouldBe("Indexing");

        await Sync(other);                                          // the passed storage has no such job
        _env.Row(id)!.CurrentState.ShouldBe("Deleted");
    }

    [Test]
    public async Task OtherStatesAreNotReconciled()
    {
        _env.SeedRow("9", Created, "Processing");
        _env.SeedRow("10", Created, "Succeeded");
        var before = (PublicationEnv.Snapshot(_env.Row("9")), PublicationEnv.Snapshot(_env.Row("10")));

        await Sync(new InMemoryStorage());

        (PublicationEnv.Snapshot(_env.Row("9")), PublicationEnv.Snapshot(_env.Row("10"))).ShouldBe(before);
    }

    [Test]
    public void SyncedStates_ContainIndexingAndCreated() =>
        JobIndexSyncService.SyncedStates.ShouldBe(["Created", "Indexing"], ignoreOrder: true);

    [Test]
    public async Task OneFailingRow_DoesNotStopTheOthers()
    {
        _env.SeedRow("1", Created, "Indexing");
        _env.SeedRow("2", Created, "Indexing");
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.UpdateStateIfCurrentAsync("1", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db"));
        writer.Setup(w => w.UpdateStateIfCurrentAsync("2", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<string, IReadOnlyCollection<string>, string, DateTime, CancellationToken>((i, e, n, c, t) =>
                _env.Writer.UpdateStateIfCurrentAsync(i, e, n, c, t));

        await Sync(new InMemoryStorage(), writer: writer.Object);

        _env.Row("2")!.CurrentState.ShouldBe("Deleted");
        _env.Row("1")!.CurrentState.ShouldBe("Indexing");
    }
}
