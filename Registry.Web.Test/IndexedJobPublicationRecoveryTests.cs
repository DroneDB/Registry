#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>Controllable clock: wall time and the monotonic timestamp advance together.</summary>
internal sealed class FakeTime : TimeProvider
{
    private DateTime _now;
    private long _timestamp;

    public FakeTime(DateTime utcNow) => _now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public DateTime Now => _now;
    public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _timestamp += by.Ticks;
    }

    /// <summary>Moves wall time (not the monotonic timestamp).</summary>
    public void Set(DateTime utcNow) => _now = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    /// <summary>Rounds up to a whole minute (set scores are whole minutes since a fixed epoch).</summary>
    public static DateTime CeilMinute(DateTime value) =>
        new(((value.Ticks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute) * TimeSpan.TicksPerMinute,
            DateTimeKind.Utc);
}

/// <summary>Records log calls so tests can assert on levels and messages.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));

    public int Count(LogLevel level) => Entries.Count(e => e.Level == level);
}

/// <summary>Generic delegating proxy used to inject faults between the code under test and a storage.</summary>
public class InterceptingProxy : DispatchProxy
{
    public object Target { get; set; } = null!;
    public Func<MethodInfo, object?[]?, (bool Handled, object? Result)>? Hook { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (Hook != null)
        {
            var (handled, result) = Hook(targetMethod!, args);
            if (handled) return result;
        }

        try
        {
            return targetMethod!.Invoke(Target, args);
        }
        catch (TargetInvocationException ex)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw;
        }
    }
}

/// <summary>Decorator over a real storage whose connections can be made to fail or lie.</summary>
internal sealed class FaultyStorage(JobStorage inner) : JobStorage
{
    public Exception? GetConnectionFailure { get; set; }
    public Func<MethodInfo, object?[]?, (bool Handled, object? Result)>? ConnectionHook { get; set; }

    public override IStorageConnection GetConnection()
    {
        if (GetConnectionFailure != null) throw GetConnectionFailure;
        var proxy = DispatchProxy.Create<IStorageConnection, InterceptingProxy>();
        var interceptor = (InterceptingProxy)(object)proxy;
        interceptor.Target = inner.GetConnection();
        interceptor.Hook = ConnectionHook;
        return proxy;
    }

    public override IMonitoringApi GetMonitoringApi() => inner.GetMonitoringApi();
    public override IEnumerable<IStateHandler> GetStateHandlers() => inner.GetStateHandlers();
#pragma warning disable CS0618
    public override IEnumerable<Hangfire.Server.IServerComponent> GetComponents() => inner.GetComponents();
#pragma warning restore CS0618
}

/// <summary>State with raw (possibly malformed) data under the <c>Indexing</c> name.</summary>
internal sealed class RawIndexingState(Dictionary<string, string>? data) : IState
{
    public string Name => IndexingState.StateName;
    public string Reason => "raw";
    public bool IsFinal => false;
    public bool IgnoreJobLoadException => false;
    public Dictionary<string, string> SerializeData() => data!;
}

/// <summary>Shared helpers for the publication tests: SQLite-backed JobIndex, InMemory Hangfire, handler scope.</summary>
internal sealed class PublicationEnv : IDisposable
{
    private readonly IStateHandler[] _previousHandlers;
    private readonly List<IDisposable> _extra = [];

    public InMemoryStorage Storage { get; } = new();
    public BackgroundJobClient Client { get; }
    public SqliteConnection Connection { get; }
    public DbContextOptions<RegistryContext> Options { get; }
    public RegistryContext Db { get; }
    public JobIndexWriter Writer { get; }
    public FakeTime Time { get; }
    public CapturingLogger<IndexedJobPublicationRecoveryService> Log { get; } = new();

    public PublicationEnv(DateTime? start = null)
    {
        _previousHandlers = GlobalStateHandlers.Handlers.ToArray();
        IndexingStateHandler.EnsureRegistered();

        Connection = new SqliteConnection("Data Source=:memory:");
        Connection.Open();
        Options = new DbContextOptionsBuilder<RegistryContext>().UseSqlite(Connection).Options;
        Db = new RegistryContext(Options);
        Db.Database.EnsureCreated();
        Writer = NewWriter(Db);
        Client = new BackgroundJobClient(Storage);
        // Aligned to a whole minute: set scores are minute-granular, so a member is due exactly
        // when its due time is not after "now" only if "now" sits on a minute boundary.
        Time = new FakeTime(start ?? FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(10)));
    }

    public static JobIndexWriter NewWriter(RegistryContext db) =>
        new(db, NullLogger<JobIndexWriter>.Instance, Mock.Of<IDistributedCache>());

    public IndexedJobPublicationRecoveryService NewService(IJobIndexWriter? writer = null, JobStorage? storage = null,
        ILogger<IndexedJobPublicationRecoveryService>? log = null) =>
        new(writer ?? Writer, log ?? Log, storage, Time);

    public T Track<T>(T disposable) where T : IDisposable
    {
        _extra.Add(disposable);
        return disposable;
    }

    public static IndexPayload Payload(string org = "org", string ds = "ds", string? path = "model.obj",
        string tool = "build", string? requestHash = "rh", string? parent = "parent-1", string? workflow = "wf-1") =>
        new(org, ds, "hash-1", "user-1", "pq", path, tool, "7", requestHash, parent, workflow);

    public static Job NewJob() => Job.FromExpression(() => HangfireUtils.DummyJob(null!));

    /// <summary>Creates a job in Indexing, as the publisher does, without publishing it.</summary>
    public string CreateIndexing(IndexedJobPublicationRecord record, JobStorage? onStorage = null)
    {
        var client = onStorage == null ? Client : new BackgroundJobClient(onStorage);
        return client.Create(NewJob(), new IndexingState(record));
    }

    public string CreateRaw(Dictionary<string, string>? data) => Client.Create(NewJob(), new RawIndexingState(data));

    public IndexedJobPublicationRecord Immediate(TimeSpan age, string queue = "critical", IndexPayload? payload = null) =>
        IndexedJobPublicationRecord.ForImmediate(payload ?? Payload(), Time.Now - age, queue);

    public IndexedJobPublicationRecord Scheduled(TimeSpan age, TimeSpan delay, IndexPayload? payload = null) =>
        IndexedJobPublicationRecord.ForScheduled(payload ?? Payload(), Time.Now - age, delay);

    public string? State(string jobId, JobStorage? s = null)
    {
        using var c = (s ?? Storage).GetConnection();
        return c.GetStateData(jobId)?.Name;
    }

    public StateData? StateData(string jobId, JobStorage? s = null)
    {
        using var c = (s ?? Storage).GetConnection();
        return c.GetStateData(jobId);
    }

    public int JobCount(JobStorage? s = null)
    {
        var mon = (s ?? Storage).GetMonitoringApi();
        var n = 0;
        while (mon.JobDetails((n + 1).ToString()) != null) n++;
        return n;
    }

    public bool InSet(string jobId, JobStorage? s = null)
    {
        using var c = (s ?? Storage).GetConnection();
        return c.GetAllItemsFromSet(IndexingStateHandler.RecoverySetKey).Contains(jobId);
    }

    public int SetSize(JobStorage? s = null)
    {
        using var c = (s ?? Storage).GetConnection();
        return c.GetAllItemsFromSet(IndexingStateHandler.RecoverySetKey).Count;
    }

    /// <summary>True when the member has exactly the given score (works when it is the lowest member).</summary>
    public bool HasScore(string jobId, double score, JobStorage? s = null)
    {
        using var c = (s ?? Storage).GetConnection();
        return c.GetFirstByLowestScoreFromSet(IndexingStateHandler.RecoverySetKey, score, score) == jobId;
    }

    public int StateHistoryCount(string jobId, string stateName) =>
        Storage.GetMonitoringApi().JobDetails(jobId).History.Count(h => h.StateName == stateName);

    public void AddMember(string jobId, double score)
    {
        using var c = Storage.GetConnection();
        using var t = c.CreateWriteTransaction();
        t.AddToSet(IndexingStateHandler.RecoverySetKey, jobId, score);
        t.Commit();
    }

    public string? Param(string jobId, string key)
    {
        using var c = Storage.GetConnection();
        return c.GetJobParameter(jobId, key);
    }

    public JobIndex? Row(string jobId)
    {
        using var db = new RegistryContext(Options);
        return db.JobIndices.AsNoTracking().FirstOrDefault(x => x.JobId == jobId);
    }

    public int RowCount()
    {
        using var db = new RegistryContext(Options);
        return db.JobIndices.Count();
    }

    /// <summary>A row with telemetry as a worker would have written it.</summary>
    public JobIndex SeedRow(string jobId, DateTime created, string state, IndexPayload? payload = null)
    {
        var p = payload ?? Payload();
        var row = new JobIndex
        {
            JobId = jobId, OrgSlug = p.OrgSlug, DsSlug = p.DsSlug, Hash = p.Hash, Path = p.Path, UserId = p.UserId,
            Queue = p.Queue, CreatedAtUtc = created, LastStateChangeUtc = created.AddSeconds(5), CurrentState = state,
            MethodDisplay = "seeded", ToolId = p.ToolId, ToolVersion = p.ToolVersion, RequestHash = p.RequestHash,
            ParentJobId = p.ParentJobId, WorkflowExecutionId = p.WorkflowExecutionId,
            ProcessingAtUtc = created.AddSeconds(6), ProgressPercent = 42, PhaseMessage = "phase",
            ArtifactSizeBytes = 123, ArtifactSha256 = "abc", ErrorType = state == "Failed" ? "Boom" : null,
            LogTailJson = "[\"l\"]", ProgressUpdatedAtUtc = created.AddSeconds(7)
        };
        if (state == "Succeeded") row.SucceededAtUtc = created.AddSeconds(8);
        if (state == "Failed") row.FailedAtUtc = created.AddSeconds(8);
        if (state == "Deleted") row.DeletedAtUtc = created.AddSeconds(8);
        using var db = new RegistryContext(Options);
        db.JobIndices.Add(row);
        db.SaveChanges();
        return row;
    }

    public static string Snapshot(JobIndex? row) =>
        row == null ? "<null>" : System.Text.Json.JsonSerializer.Serialize(row);

    public void Dispose()
    {
        foreach (var d in _extra) d.Dispose();
        Db.Dispose();
        Connection.Dispose();
        GlobalStateHandlers.Handlers.Clear();
        foreach (var h in _previousHandlers) GlobalStateHandlers.Handlers.Add(h);
    }
}

[TestFixture]
[NonParallelizable]
public class IndexedJobPublicationRecoveryTests
{
    private PublicationEnv _env = null!;
    private IndexedJobPublicationRecoveryService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _env = new PublicationEnv();
        _service = _env.NewService();
    }

    [TearDown]
    public void TearDown() => _env.Dispose();

    private Task<RecoveryPassSummary> Pass(IndexedJobPublicationRecoveryService? service = null,
        IBackgroundJobClient? client = null, JobStorage? storage = null, CancellationToken ct = default) =>
        (service ?? _service).RunPassAsync(storage ?? _env.Storage, client ?? _env.Client, ct);

    // ---------------------------------------------------------------- A1: interrupted publication completes on the same id

    [Test]
    public async Task InterruptedAfterCommit_PassPublishesSameJobId_NoReplacementJob()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(3), "critical");
        var id = _env.CreateIndexing(record);
        _env.State(id).ShouldBe("Indexing");
        _env.InSet(id).ShouldBeTrue("the Indexing transaction itself registered the recovery member");

        var summary = await Pass();

        summary.Examined.ShouldBe(1);
        summary.Published.ShouldBe(1);
        summary.StopReason.ShouldBe(RecoveryStopReason.Drained);
        _env.JobCount().ShouldBe(1, "recovery must reuse the job, not create a replacement");
        var state = _env.StateData(id)!;
        state.Name.ShouldBe("Enqueued");
        state.Data["Queue"].ShouldBe("critical");
        _env.InSet(id).ShouldBeFalse();

        // Params and row come from the record.
        _env.Param(id, JobParamKeys.OrgSlug).ShouldBe("org");
        _env.Param(id, JobParamKeys.DsSlug).ShouldBe("ds");
        _env.Param(id, JobParamKeys.Path).ShouldBe("model.obj");
        _env.Param(id, JobParamKeys.Hash).ShouldBe("hash-1");
        _env.Param(id, JobParamKeys.UserId).ShouldBe("user-1");
        _env.Param(id, JobParamKeys.Queue).ShouldBe("pq");
        var row = _env.Row(id)!;
        row.CreatedAtUtc.ShouldBe(record.CreatedAtUtc);
        row.RequestHash.ShouldBe("rh");
        row.ParentJobId.ShouldBe("parent-1");
        row.WorkflowExecutionId.ShouldBe("wf-1");
        row.ToolId.ShouldBe("build");
        row.ToolVersion.ShouldBe("7");
        row.CurrentState.ShouldBe("Indexing", "no JobIndexStateFilter is installed in this fixture");
    }

    [Test]
    public async Task InterruptedAfterPartialParameterWrite_PassCompletesAllParameters()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        using (var c = _env.Storage.GetConnection())
            c.SetJobParameter(id, JobParamKeys.OrgSlug, "org");

        var summary = await Pass();

        summary.Published.ShouldBe(1);
        _env.Param(id, JobParamKeys.DsSlug).ShouldBe("ds");
        _env.Param(id, JobParamKeys.Path).ShouldBe("model.obj");
        _env.Param(id, JobParamKeys.UserId).ShouldBe("user-1");
        _env.State(id).ShouldBe("Enqueued");
    }

    [Test]
    public async Task InterruptedAfterJobIndex_PassKeepsRowAndPublishes()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var id = _env.CreateIndexing(record);
        await _env.Writer.PrepareForPublicationAsync(id, record.Payload, record.CreatedAtUtc, "m");
        var before = PublicationEnv.Snapshot(_env.Row(id));

        var summary = await Pass();

        summary.Published.ShouldBe(1);
        PublicationEnv.Snapshot(_env.Row(id)).ShouldBe(before);
        _env.State(id).ShouldBe("Enqueued");
    }

    [TestCase("Processing")]
    [TestCase("Succeeded")]
    [TestCase("Failed")]
    [TestCase("Deleted")]
    public async Task RecoveryNeverResetsRowAdvancedByAWorker(string rowState)
    {
        // A4: the row of this very publication reached a later state (e.g. recovery was
        // interrupted after dispatch and a worker ran the job); a repeated pass must not touch it.
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var id = _env.CreateIndexing(record);
        _env.SeedRow(id, record.CreatedAtUtc, rowState, record.Payload);
        var before = PublicationEnv.Snapshot(_env.Row(id));

        var summary = await Pass();

        summary.Published.ShouldBe(1);
        PublicationEnv.Snapshot(_env.Row(id)).ShouldBe(before);

        // Converges: nothing left to do.
        (await Pass()).Examined.ShouldBe(0);
        PublicationEnv.Snapshot(_env.Row(id)).ShouldBe(before);
    }

    [Test]
    public async Task MemberNotYetDue_IsLeftUntouched()
    {
        // Created 1 minute ago: due in 1 more minute.
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(1)));

        var summary = await Pass();

        summary.Examined.ShouldBe(0);
        _env.State(id).ShouldBe("Indexing");
        _env.InSet(id).ShouldBeTrue();
        _env.Row(id).ShouldBeNull();
        _env.Param(id, JobParamKeys.OrgSlug).ShouldBeNull();

        _env.Time.Advance(TimeSpan.FromMinutes(1));
        (await Pass()).Published.ShouldBe(1);
    }

    // ---------------------------------------------------------------- destinations

    [Test]
    public async Task ScheduledFuture_KeepsAbsoluteTime_NotRestartedFromRecovery()
    {
        var record = _env.Scheduled(TimeSpan.FromMinutes(3), TimeSpan.FromHours(2));
        var id = _env.CreateIndexing(record);

        (await Pass()).Published.ShouldBe(1);

        var state = _env.StateData(id)!;
        state.Name.ShouldBe("Scheduled");
        JobHelper.DeserializeDateTime(state.Data["EnqueueAt"]).ShouldBe(record.EnqueueAtUtc!.Value);
        record.EnqueueAtUtc!.Value.ShouldBe(record.CreatedAtUtc.AddHours(2));
        // Not "recovery time + 2h".
        record.EnqueueAtUtc!.Value.ShouldBeLessThan(_env.Time.Now.AddHours(2).AddMinutes(-2));
    }

    [Test]
    public async Task ScheduledPastDue_IsRestoredAsPastDue_NotPushedForward()
    {
        var record = _env.Scheduled(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));
        var id = _env.CreateIndexing(record);

        (await Pass()).Published.ShouldBe(1);

        var state = _env.StateData(id)!;
        state.Name.ShouldBe("Scheduled");
        var at = JobHelper.DeserializeDateTime(state.Data["EnqueueAt"]);
        at.ShouldBe(record.EnqueueAtUtc!.Value);
        at.ShouldBeLessThan(_env.Time.Now);
    }

    [Test]
    public async Task ImmediateQueueIsPreserved()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3), "special_queue"));
        (await Pass()).Published.ShouldBe(1);
        _env.StateData(id)!.Data["Queue"].ShouldBe("special_queue");
    }

    // ---------------------------------------------------------------- A3: competing publishers

    [Test]
    public async Task SequentialPasses_OneTransition_RowNotReset()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));

        var first = await Pass();
        var rowAfterFirst = PublicationEnv.Snapshot(_env.Row(id));
        var second = await Pass();

        first.Published.ShouldBe(1);
        second.Examined.ShouldBe(0);
        _env.StateHistoryCount(id, "Enqueued").ShouldBe(1);
        PublicationEnv.Snapshot(_env.Row(id)).ShouldBe(rowAfterFirst);
    }

    [Test]
    public async Task CompetingPublisherWinsDuringPass_NoSecondTransition_MemberRemoved_RowKept()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var id = _env.CreateIndexing(record);
        var telemetryRow = _env.SeedRow(id, record.CreatedAtUtc, "Processing", record.Payload);
        var before = PublicationEnv.Snapshot(_env.Row(id));

        // While recovery prepares the JobIndex, the slow publisher completes the transition.
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.PrepareForPublicationAsync(id, It.IsAny<IndexPayload>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, IndexPayload, DateTime, string, CancellationToken>(async (i, p, c, m, t) =>
            {
                var result = await _env.Writer.PrepareForPublicationAsync(i, p, c, m, t);
                _env.Client.ChangeState(i, new EnqueuedState("critical"), "Indexing").ShouldBeTrue();
                return result;
            });
        var service = _env.NewService(writer.Object);

        var summary = await Pass(service);

        summary.Published.ShouldBe(0);
        summary.StaleRemoved.ShouldBe(1);
        _env.StateHistoryCount(id, "Enqueued").ShouldBe(1);
        _env.InSet(id).ShouldBeFalse();
        PublicationEnv.Snapshot(_env.Row(id)).ShouldBe(before);
        telemetryRow.ProgressPercent.ShouldBe(42);
    }

    [Test]
    public async Task SlowPublisherAfterRecovery_ReturnsIdWithoutError_ExactlyOneTransition()
    {
        var created = new List<string>();
        var enqueuerLog = new CapturingLogger<IndexedJobEnqueuer>();
        _env.Time.Set(FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(10)));

        // The publisher's own JobIndex preparation runs after a recovery pass already published the job.
        var hooked = new Mock<IJobIndexWriter>();
        hooked.Setup(w => w.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(),
                It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, IndexPayload, DateTime, string, CancellationToken>(async (i, p, c, m, t) =>
            {
                created.Add(i);
                (await Pass()).Published.ShouldBe(1);
                return await _env.Writer.PrepareForPublicationAsync(i, p, c, m, t);
            });
        using var provider = new ServiceCollection()
            .AddSingleton(hooked.Object).BuildServiceProvider();
        var enqueuer = new IndexedJobEnqueuer(_env.Client, provider, enqueuerLog, _env.Storage);

        var id = enqueuer.Enqueue(() => HangfireUtils.DummyJob(null!), PublicationEnv.Payload());

        id.ShouldBe(created.Single());
        _env.State(id).ShouldBe("Enqueued");
        _env.StateHistoryCount(id, "Enqueued").ShouldBe(1);
        _env.StateHistoryCount(id, "Deleted").ShouldBe(0);
        enqueuerLog.Count(LogLevel.Error).ShouldBe(0);
        _env.JobCount().ShouldBe(1);
    }

    [Test]
    public async Task TwoParallelRecoveryPasses_ExactlyOneWins()
    {
        // Real concurrency needs a file database (an in-memory connection is single-threaded).
        var path = Path.Combine(Path.GetTempPath(), "recovery-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<RegistryContext>().UseSqlite("Data Source=" + path).Options;
            using (var init = new RegistryContext(options)) init.Database.EnsureCreated();
            using var db1 = new RegistryContext(options);
            using var db2 = new RegistryContext(options);
            var s1 = _env.NewService(PublicationEnv.NewWriter(db1));
            var s2 = _env.NewService(PublicationEnv.NewWriter(db2));

            const int jobs = 6;
            var ids = Enumerable.Range(0, jobs)
                .Select(_ => _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)))).ToList();

            var results = await Task.WhenAll(
                Task.Run(() => s1.RunPassAsync(_env.Storage, new BackgroundJobClient(_env.Storage), default)),
                Task.Run(() => s2.RunPassAsync(_env.Storage, new BackgroundJobClient(_env.Storage), default)));

            // Every job is published exactly once, whoever got it.
            ids.ForEach(i => _env.StateHistoryCount(i, "Enqueued").ShouldBe(1));
            results.Sum(r => r.Published).ShouldBe(jobs);
            _env.SetSize().ShouldBe(0);
            using var check = new RegistryContext(options);
            check.JobIndices.Count().ShouldBe(jobs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- stale members / Deleted

    [Test]
    public async Task StaleMembers_AreRemoved_DeletedJobIsNeverRevived()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var published = _env.CreateIndexing(record);
        _env.Client.ChangeState(published, new EnqueuedState("critical"), "Indexing").ShouldBeTrue();
        var deleted = _env.CreateIndexing(record);
        _env.Client.ChangeState(deleted, new DeletedState { Reason = "admin" }, "Indexing").ShouldBeTrue();
        _env.InSet(published).ShouldBeFalse("leaving Indexing unapplies the member");

        // A score update may re-add the member of a job that just left Indexing.
        _env.AddMember(published, 0);
        _env.AddMember(deleted, 0);
        _env.AddMember("9999", 0);

        var summary = await Pass();

        summary.Examined.ShouldBe(3);
        summary.StaleRemoved.ShouldBe(3);
        summary.Published.ShouldBe(0);
        _env.SetSize().ShouldBe(0);
        _env.State(deleted).ShouldBe("Deleted");
        _env.StateHistoryCount(deleted, "Enqueued").ShouldBe(0);
        _env.State(published).ShouldBe("Enqueued");
        _env.RowCount().ShouldBe(0, "stale members must not upsert JobIndex rows");
    }

    // ---------------------------------------------------------------- invalid records

    [Test]
    public async Task MalformedRecord_CancelsJob_MarksExistingRowDeletedWithErrorType()
    {
        var id = _env.CreateRaw(new Dictionary<string, string>
        {
            [IndexedJobPublicationRecord.SchemaVersionKey] = "1",
            [IndexedJobPublicationRecord.RecordKey] = "{ this is not json"
        });
        var created = _env.StateData(id)!;
        _env.SeedRow(id, DateTime.UtcNow.AddMinutes(-9), "Indexing");
        _env.Time.Set(FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(5)));

        var summary = await Pass();

        summary.Cancelled.ShouldBe(1);
        var state = _env.StateData(id)!;
        state.Name.ShouldBe("Deleted");
        state.Reason.ShouldNotBeNull();
        state.Reason!.ShouldContain("Invalid publication record");
        var row = _env.Row(id)!;
        row.CurrentState.ShouldBe("Deleted");
        row.ErrorType.ShouldBe("InvalidPublicationRecord");
        row.DeletedAtUtc.ShouldNotBeNull();
        _env.InSet(id).ShouldBeFalse();
        _env.Param(id, JobParamKeys.OrgSlug).ShouldBeNull("nothing is published for an invalid record");
        _env.Log.Count(LogLevel.Error).ShouldBeGreaterThan(0);
        _env.Log.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains(id));
        created.Name.ShouldBe("Indexing");
    }

    [Test]
    public async Task MalformedRecord_WithoutRow_CreatesNoRow()
    {
        var id = _env.CreateRaw(new Dictionary<string, string>());
        _env.Time.Set(FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(5)));

        var summary = await Pass();

        summary.Cancelled.ShouldBe(1);
        _env.State(id).ShouldBe("Deleted");
        _env.RowCount().ShouldBe(0);
    }

    [Test]
    public async Task UnsupportedDestination_IsCancelledAsInvalid()
    {
        var good = _env.Immediate(TimeSpan.FromMinutes(3));
        var data = good.ToStateData();
        data[IndexedJobPublicationRecord.RecordKey] =
            data[IndexedJobPublicationRecord.RecordKey].Replace("\"Immediate\"", "\"Continuation\"");
        var id = _env.CreateRaw(data);
        _env.Time.Set(FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(5)));

        (await Pass()).Cancelled.ShouldBe(1);
        _env.State(id).ShouldBe("Deleted");
    }

    [Test]
    public async Task IncompatibleRow_CancelsJob_ButNeverModifiesRow()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var id = _env.CreateIndexing(record);
        // Same creation time, different identity: a row that belongs to another publication.
        var other = PublicationEnv.Payload(org: "other-org");
        _env.SeedRow(id, record.CreatedAtUtc, "Processing", other);
        var before = PublicationEnv.Snapshot(_env.Row(id));

        var summary = await Pass();

        summary.Cancelled.ShouldBe(1);
        _env.State(id).ShouldBe("Deleted");
        PublicationEnv.Snapshot(_env.Row(id)).ShouldBe(before);
        _env.Row(id)!.ErrorType.ShouldBeNull();
        _env.InSet(id).ShouldBeFalse();
    }

    [Test]
    public async Task NewerSchema_StaysIndexing_Deferred_NotCancelled_OneWarning()
    {
        var id = _env.CreateRaw(new Dictionary<string, string>
        {
            [IndexedJobPublicationRecord.SchemaVersionKey] = "2",
            [IndexedJobPublicationRecord.RecordKey] = "{\"schemaVersion\":2,\"somethingNew\":[1,2]}"
        });
        _env.Time.Set(FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(3)));

        var summary = await Pass();

        summary.Deferred.ShouldBe(1);
        summary.Cancelled.ShouldBe(0);
        _env.State(id).ShouldBe("Indexing");
        _env.Log.Count(LogLevel.Warning).ShouldBe(1);
        _env.Log.Count(LogLevel.Error).ShouldBe(0);
        _env.Param(id, JobParamKeys.OrgSlug).ShouldBeNull();
        _env.RowCount().ShouldBe(0);
        // Age is between 3 and 4 minutes -> delay 2 minutes; ceil to whole minutes.
        _env.InSet(id).ShouldBeTrue();
        _env.HasScore(id, IndexingStateHandler.ToScore(_env.Time.Now.AddMinutes(2))).ShouldBeTrue();

        // Still due again later, and warns once per retry.
        (await Pass()).Examined.ShouldBe(0);
        _env.Time.Advance(TimeSpan.FromMinutes(2));
        (await Pass()).Deferred.ShouldBe(1);
        _env.Log.Count(LogLevel.Warning).ShouldBe(2);
        _env.State(id).ShouldBe("Indexing");
    }

    [Test]
    public async Task UnloadableJob_IsDeferred_WithoutAnyWrites()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var id = _env.CreateIndexing(record);
        var writer = new Mock<IJobIndexWriter>(MockBehavior.Strict);
        var faulty = new FaultyStorage(_env.Storage)
        {
            ConnectionHook = (m, a) => m.Name == "GetJobData"
                ? (true, new JobData
                {
                    Job = null, LoadException = new JobLoadException("type missing", new TypeLoadException()),
                    State = "Indexing", CreatedAt = record.CreatedAtUtc
                })
                : (false, null)
        };
        var service = _env.NewService(writer.Object);

        var summary = await Pass(service, new BackgroundJobClient(faulty), faulty);

        summary.Deferred.ShouldBe(1);
        summary.Published.ShouldBe(0);
        _env.State(id).ShouldBe("Indexing");
        _env.Param(id, JobParamKeys.OrgSlug).ShouldBeNull();
        writer.VerifyNoOtherCalls();
        _env.InSet(id).ShouldBeTrue();
        _env.Log.Count(LogLevel.Warning).ShouldBeGreaterThan(0);
    }

    // ---------------------------------------------------------------- storage outage

    [Test]
    public async Task StorageOutage_NoDeletion_MemberKept_StopReasonStorageError()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        var faulty = new FaultyStorage(_env.Storage) { GetConnectionFailure = new InvalidOperationException("db down") };

        var summary = await Pass(storage: faulty, client: new BackgroundJobClient(_env.Storage));

        summary.StopReason.ShouldBe(RecoveryStopReason.StorageError);
        summary.Cancelled.ShouldBe(0);
        summary.Examined.ShouldBe(0);
        _env.State(id).ShouldBe("Indexing");
        _env.InSet(id).ShouldBeTrue();

        // Storage is back: recovery continues.
        (await Pass()).Published.ShouldBe(1);
    }

    [Test]
    public async Task StorageOutageWhileProcessing_ThenWriteFailure_EndsPassWithoutDeleting()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        var id2 = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        var faulty = new FaultyStorage(_env.Storage)
        {
            ConnectionHook = (m, _) => m.Name switch
            {
                "GetStateData" => throw new InvalidOperationException("read failed"),
                "CreateWriteTransaction" => throw new InvalidOperationException("write failed"),
                _ => (false, null)
            }
        };

        var summary = await Pass(storage: faulty, client: new BackgroundJobClient(_env.Storage));

        summary.StopReason.ShouldBe(RecoveryStopReason.StorageError);
        summary.Examined.ShouldBe(1);
        _env.State(id).ShouldBe("Indexing");
        _env.State(id2).ShouldBe("Indexing");
        _env.SetSize().ShouldBe(2);
        _env.RowCount().ShouldBe(0);
    }

    [Test]
    public async Task TransientReadFailure_DefersCandidate_AndKeepsItInTheSet()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        var fail = true;
        var faulty = new FaultyStorage(_env.Storage)
        {
            ConnectionHook = (m, _) => fail && m.Name == "GetStateData"
                ? throw new InvalidOperationException("read failed")
                : (false, null)
        };

        var summary = await Pass(storage: faulty, client: new BackgroundJobClient(_env.Storage));

        summary.Deferred.ShouldBe(1);
        _env.State(id).ShouldBe("Indexing");
        _env.InSet(id).ShouldBeTrue();
        // Observation (reported to the coordinator): when the job itself cannot be read, its age is unknown
        // and the delay falls back to the first rung (1 minute) instead of following the age ladder.
        _env.HasScore(id, IndexingStateHandler.ToScore(_env.Time.Now.AddMinutes(1))).ShouldBeTrue();
        fail = false;
        _env.Time.Advance(TimeSpan.FromMinutes(1));
        (await Pass(storage: faulty, client: new BackgroundJobClient(_env.Storage))).Published.ShouldBe(1);
    }

    [Test]
    public async Task CancelFailureDuringOutage_DoesNotDeleteInvalidJob()
    {
        var id = _env.CreateRaw(new Dictionary<string, string>());
        _env.Time.Set(FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(5)));
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("storage down"));

        var summary = await Pass(client: client.Object);

        summary.Cancelled.ShouldBe(0);
        summary.Deferred.ShouldBe(1);
        _env.State(id).ShouldBe("Indexing");
        _env.InSet(id).ShouldBeTrue();
    }

    [Test]
    public async Task TransientWriterFailure_IsNotClassifiedInvalid_JobStaysIndexing()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(),
                It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var summary = await Pass(_env.NewService(writer.Object));

        summary.Deferred.ShouldBe(1);
        summary.Cancelled.ShouldBe(0);
        _env.State(id).ShouldBe("Indexing");
        _env.StateHistoryCount(id, "Failed").ShouldBe(0);
        writer.Verify(w => w.UpdateStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- bounds and fairness

    [Test]
    public async Task MoreThanOneHundredDueCandidates_AreProcessedInBoundedPasses()
    {
        const int total = 130;
        var ids = new List<string>();
        for (var i = 0; i < total; i++)
            ids.Add(_env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3),
                payload: PublicationEnv.Payload(path: $"m{i}.obj"))));

        var first = await Pass();
        first.Examined.ShouldBe(IndexedJobPublicationRecoveryService.MaxCandidatesPerPass);
        first.Published.ShouldBe(100);
        first.StopReason.ShouldBe(RecoveryStopReason.CandidateLimit);
        _env.SetSize().ShouldBe(30);

        var second = await Pass();
        second.Examined.ShouldBe(30);
        second.Published.ShouldBe(30);
        second.StopReason.ShouldBe(RecoveryStopReason.Drained);

        ids.All(i => _env.State(i) == "Enqueued").ShouldBeTrue();
        _env.JobCount().ShouldBe(total);
        _env.RowCount().ShouldBe(total);
    }

    [Test]
    public async Task FailingMembers_AreDeferredBehindOthers_AndDoNotStarveThem()
    {
        // Older jobs (lowest scores) fail; younger ones must still be published in the same pass.
        var failing = new[]
        {
            _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(30))),
            _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(45)))
        };
        var healthy = Enumerable.Range(0, 3)
            .Select(_ => _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)))).ToList();
        var fixedBug = false;
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(),
                It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, IndexPayload, DateTime, string, CancellationToken>((i, p, c, m, t) =>
                !fixedBug && failing.Contains(i)
                    ? throw new InvalidOperationException("poison")
                    : _env.Writer.PrepareForPublicationAsync(i, p, c, m, t));
        var service = _env.NewService(writer.Object);

        var summary = await Pass(service);

        summary.Examined.ShouldBe(5);
        summary.Deferred.ShouldBe(2);
        summary.Published.ShouldBe(3);
        summary.StopReason.ShouldBe(RecoveryStopReason.Drained);
        healthy.All(i => _env.State(i) == "Enqueued").ShouldBeTrue();
        failing.All(i => _env.State(i) == "Indexing" && _env.InSet(i)).ShouldBeTrue();
        // Age 30 min -> 20 min delay; age 45 min -> 40 min delay.
        _env.HasScore(failing[0], IndexingStateHandler.ToScore(_env.Time.Now.AddMinutes(20))).ShouldBeTrue();
        _env.HasScore(failing[1], IndexingStateHandler.ToScore(_env.Time.Now.AddMinutes(40))).ShouldBeTrue();

        // Not retried within the same pass nor before the delay.
        (await Pass(service)).Examined.ShouldBe(0);
        fixedBug = true;
        _env.Time.Advance(TimeSpan.FromMinutes(20));
        (await Pass(service)).Published.ShouldBe(1);
        _env.Time.Advance(TimeSpan.FromMinutes(20));
        (await Pass(service)).Published.ShouldBe(1);
    }

    [Test]
    public async Task TimeBudget_StopsPassBetweenCandidates()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)))).ToList();
        var faulty = new FaultyStorage(_env.Storage)
        {
            ConnectionHook = (m, _) =>
            {
                if (m.Name == "GetStateData") _env.Time.Advance(IndexedJobPublicationRecoveryService.PassBudget +
                                                               TimeSpan.FromSeconds(1));
                return (false, null);
            }
        };

        var summary = await Pass(storage: faulty, client: new BackgroundJobClient(_env.Storage));

        summary.Examined.ShouldBe(1);
        summary.StopReason.ShouldBe(RecoveryStopReason.TimeBudget);
        ids.Count(i => _env.State(i) == "Indexing").ShouldBe(2);
    }

    [Test]
    public async Task CancelledToken_StopsBeforeExamining()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var summary = await Pass(ct: cts.Token);

        summary.StopReason.ShouldBe(RecoveryStopReason.Cancelled);
        summary.Examined.ShouldBe(0);
        _env.State(id).ShouldBe("Indexing");
    }

    [Test]
    public async Task RepeatedMember_EndsPassWithRepeatGuard()
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(),
            It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("x"));
        var faulty = new FaultyStorage(_env.Storage)
        {
            ConnectionHook = (m, _) => m.Name == "GetFirstByLowestScoreFromSet" ? (true, id) : (false, null)
        };

        var summary = await Pass(_env.NewService(writer.Object), new BackgroundJobClient(_env.Storage), faulty);

        summary.Examined.ShouldBe(1);
        summary.StopReason.ShouldBe(RecoveryStopReason.RepeatGuard);
    }

    // ---------------------------------------------------------------- retry ladder

    [TestCase(0, 1)]
    [TestCase(119, 1)]
    [TestCase(179, 1)]
    [TestCase(180, 2)]
    [TestCase(299, 2)]
    [TestCase(300, 5)]
    [TestCase(599, 5)]
    [TestCase(600, 10)]
    [TestCase(1199, 10)]
    [TestCase(1200, 20)]
    [TestCase(2399, 20)]
    [TestCase(2400, 40)]
    [TestCase(4799, 40)]
    [TestCase(4800, 60)]
    [TestCase(100000, 60)]
    public void RetryDelayFor_FollowsTheSpecTable(int ageSeconds, int expectedMinutes) =>
        IndexedJobPublicationRecoveryService.RetryDelayFor(TimeSpan.FromSeconds(ageSeconds))
            .ShouldBe(TimeSpan.FromMinutes(expectedMinutes));

    [TestCase(3, 2)]
    [TestCase(5, 5)]
    [TestCase(10, 10)]
    [TestCase(20, 20)]
    [TestCase(40, 40)]
    [TestCase(80, 60)]
    [TestCase(500, 60)]
    public async Task DeferredMember_IsRescheduledByAgeOfTheOriginalCreation(int ageMinutes, int delayMinutes)
    {
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(ageMinutes)));
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(),
            It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("x"));

        await Pass(_env.NewService(writer.Object));

        _env.HasScore(id, IndexingStateHandler.ToScore(_env.Time.Now.AddMinutes(delayMinutes))).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- id reuse and configured storage

    [Test]
    public async Task LeftoverRowFromEarlierStorageIncarnation_IsReplacedByFreshIndexingRow()
    {
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var id = _env.CreateIndexing(record);
        _env.SeedRow(id, record.CreatedAtUtc.AddDays(-5), "Succeeded", PublicationEnv.Payload(org: "old-org"));

        (await Pass()).Published.ShouldBe(1);

        var row = _env.Row(id)!;
        row.OrgSlug.ShouldBe("org");
        row.CreatedAtUtc.ShouldBe(record.CreatedAtUtc);
        row.CurrentState.ShouldBe("Indexing");
        row.SucceededAtUtc.ShouldBeNull();
        row.ProgressPercent.ShouldBeNull();
        row.ArtifactSha256.ShouldBeNull();
    }

    [Test]
    public async Task ConfiguredStorage_IsUsed_NotJobStorageCurrent()
    {
        var other = new InMemoryStorage();
        using var scope = new JobStorageScope(other);
        var record = _env.Immediate(TimeSpan.FromMinutes(3));
        var id = _env.CreateIndexing(record);   // on _env.Storage

        var service = new IndexedJobPublicationRecoveryService(_env.Writer, _env.Log, _env.Storage, _env.Time);
        await service.RecoverPublications(null);

        _env.State(id).ShouldBe("Enqueued");
        _env.JobCount(other).ShouldBe(0);
        _env.SetSize(other).ShouldBe(0);
    }

    [Test]
    public async Task RunPass_WithStorageDifferentFromCurrent_PublishesOnPassedStorage()
    {
        var other = new InMemoryStorage();
        using var scope = new JobStorageScope(other);
        var otherId = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)), other);   // decoy on Current
        var id = _env.CreateIndexing(_env.Immediate(TimeSpan.FromMinutes(3)));

        var summary = await Pass();

        summary.Published.ShouldBe(1);
        _env.State(id).ShouldBe("Enqueued");
        _env.State(otherId, other).ShouldBe("Indexing");
        _env.SetSize(other).ShouldBe(1);
    }
}
