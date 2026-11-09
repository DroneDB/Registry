using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The write record document (FR-XC-27, D-045): its JSON, its state read alone, records of no release.</summary>
[TestFixture]
public class TaskWriteRecordDocumentTests
{
    private static readonly DatasetKey Dataset = new("org", Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Test]
    public void ARecord_RoundTrips_WithItsStateFirst()
    {
        var record = TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset("org", Dataset.InternalRef, "new-ds"));
        record.Attempt = 2;
        record.State = TaskWriteState.CancelRequested;
        record.Add(Dataset, ["a.tif", "b/c.tif", "a.tif"], ["b"]);

        var json = record.ToJson();
        json.ShouldStartWith("{\"state\":\"cancelRequested\"");

        var read = TaskWriteRecord.Parse(json)!;
        read.State.ShouldBe(TaskWriteState.CancelRequested);
        read.Attempt.ShouldBe(2);
        read.CreatedDataset.ShouldBe(new TaskCreatedDataset("org", Dataset.InternalRef, "new-ds"));
        read.Datasets.Single().Paths.ShouldBe(["a.tif", "b/c.tif"]);
        read.Datasets.Single().Folders.ShouldBe(["b"]);
        read.HasWrites.ShouldBeTrue();
        TaskWriteRecord.StateOf(json).ShouldBe(TaskWriteState.CancelRequested);
    }

    [Test]
    public void TheStateOfALongRecord_IsReadFromItsFirstBytes()
    {
        var record = new TaskWriteRecord { State = TaskWriteState.Committed };
        record.Add(Dataset, Enumerable.Range(0, 2000).Select(i => $"folder/file-{i}.tif"), []);

        TaskWriteRecord.IsCommitted(record.ToJson()).ShouldBeTrue();
        TaskWriteRecord.StateOf(null).ShouldBeNull();
    }

    [TestCase("{\"paths\":[]}")]
    [TestCase("not json")]
    [TestCase("{\"datasets\":null}")]
    public void ARecordOfNoRelease_IsAnEmptyOpenOne(string json)
    {
        var record = TaskWriteRecord.Parse(json)!;

        record.State.ShouldBe(TaskWriteState.Open);
        record.HasWrites.ShouldBeFalse();
        record.CreatedDataset.ShouldBeNull();
        TaskWriteRecord.IsCommitted(json).ShouldBeFalse();
    }
}

/// <summary>Shared by the store and rollback fixtures: a registry database with one org and its datasets.</summary>
internal sealed class WriteRecordEnv : IDisposable
{
    public const string Org = "org";

    private readonly TestDb<RegistryContext> _db;

    public WriteRecordEnv(IInterceptor? interceptor = null)
    {
        _db = interceptor is null
            ? TestDb.Create<RegistryContext>()
            : TestDb.Create<RegistryContext>(o => o.AddInterceptors(interceptor));
        Db = _db.NewContext();
        Db.Organizations.Add(new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow, Datasets = [] });
        Db.SaveChanges();
        Store = NewStore(Db);
    }

    public RegistryContext Db { get; }
    public TaskWriteRecordStore Store { get; }
    public DbContextOptions<RegistryContext> Options => _db.Options;

    public static TaskWriteRecordStore NewStore(RegistryContext db) =>
        new(db, Mock.Of<IRecordCache>(), NullLogger<TaskWriteRecordStore>.Instance);

    public RegistryContext NewContext() => _db.NewContext();

    public Guid AddDataset(string slug)
    {
        var internalRef = Guid.NewGuid();
        var org = Db.Organizations.Single(o => o.Slug == Org);
        Db.Datasets.Add(new Dataset
            { Slug = slug, InternalRef = internalRef, CreationDate = DateTime.UtcNow, Organization = org });
        Db.SaveChanges();
        return internalRef;
    }

    public void SeedTask(string jobId, string state, string? record = null, string ds = "ds", string? userId = "user-1")
    {
        Db.JobIndices.Add(new JobIndex
        {
            JobId = jobId, OrgSlug = Org, DsSlug = ds, ToolId = "import-dataset", ToolVersion = "1",
            CurrentState = state, CreatedAtUtc = DateTime.UtcNow, LastStateChangeUtc = DateTime.UtcNow,
            UserId = userId, WriteRecordJson = record
        });
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
    }

    public string? RecordJson(string jobId)
    {
        using var db = NewContext();
        return db.JobIndices.AsNoTracking().Single(j => j.JobId == jobId).WriteRecordJson;
    }

    public TaskWriteRecord? Record(string jobId) => TaskWriteRecord.Parse(RecordJson(jobId));

    public void Dispose()
    {
        Db.Dispose();
        _db.Dispose();
    }
}

/// <summary>
/// Runs <see cref="Competitor"/> once, right before the next update of a write record reaches the database: the update
/// then finds the record changed (a lost compare-and-swap).
/// </summary>
internal sealed class CompetingWriteInterceptor : DbCommandInterceptor
{
    public Func<Task>? Competitor { get; set; }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Competitor is { } competitor && command.CommandText.Contains("WriteRecordJson"))
        {
            Competitor = null;
            await competitor();
        }

        return result;
    }
}

/// <summary>
/// The store of the write record: every change is a compare-and-swap on the whole string, retried when another writer
/// changed it first, so a commit and a cancel never both win; the resets of the task record clear it.
/// </summary>
[TestFixture]
public class TaskWriteRecordStoreTests
{
    private static readonly DatasetKey Dataset = new(WriteRecordEnv.Org, Guid.NewGuid());

    [Test]
    public async Task Paths_AreAppendedOnce_FromNoRecord()
    {
        using var env = new WriteRecordEnv();
        env.SeedTask("1", "Processing");

        await env.Store.RecordAsync("1", Dataset, ["a.tif"], ["sub"]);
        await env.Store.RecordAsync("1", Dataset, ["a.tif", "sub/b.tif"], []);

        var record = env.Record("1")!;
        record.State.ShouldBe(TaskWriteState.Open);
        record.Datasets.Single().Paths.ShouldBe(["a.tif", "sub/b.tif"]);
        record.Datasets.Single().Folders.ShouldBe(["sub"]);
    }

    [Test]
    public async Task ALostCompareAndSwap_IsRetried_AndKeepsTheOtherWrite()
    {
        var competing = new CompetingWriteInterceptor();
        using var env = new WriteRecordEnv(competing);
        env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        competing.Competitor = async () =>
        {
            await using var other = env.NewContext();
            await WriteRecordEnv.NewStore(other).RecordAsync("1", Dataset, ["other.tif"], []);
        };

        await env.Store.RecordAsync("1", Dataset, ["mine.tif"], []);

        env.Record("1")!.Datasets.Single().Paths.ShouldBe(["other.tif", "mine.tif"], ignoreOrder: true);
    }

    [Test]
    public async Task ACancelThatWinsTheRace_RefusesTheCommit()
    {
        var competing = new CompetingWriteInterceptor();
        using var env = new WriteRecordEnv(competing);
        env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        competing.Competitor = async () =>
        {
            await using var other = env.NewContext();
            (await WriteRecordEnv.NewStore(other).RequestCancelAsync("1")).ShouldBe(TaskCancelRequestResult.Requested);
        };

        (await env.Store.MarkCommittedAsync("1")).ShouldBeFalse();

        env.Record("1")!.State.ShouldBe(TaskWriteState.CancelRequested);
    }

    [Test]
    public async Task ACommitThatWinsTheRace_RefusesTheCancel()
    {
        var competing = new CompetingWriteInterceptor();
        using var env = new WriteRecordEnv(competing);
        env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        competing.Competitor = async () =>
        {
            await using var other = env.NewContext();
            (await WriteRecordEnv.NewStore(other).MarkCommittedAsync("1")).ShouldBeTrue();
        };

        (await env.Store.RequestCancelAsync("1")).ShouldBe(TaskCancelRequestResult.Committed);

        env.Record("1")!.State.ShouldBe(TaskWriteState.Committed);
    }

    [Test]
    public async Task AClaimedRecord_RefusesNewPaths_AndACommit()
    {
        using var env = new WriteRecordEnv();
        env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());

        (await env.Store.ClaimRollbackAsync("1")).ShouldNotBeNull();

        await Should.ThrowAsync<TaskWriteRecordClosedException>(() =>
            env.Store.RecordAsync("1", Dataset, ["late.tif"], []));
        (await env.Store.MarkCommittedAsync("1")).ShouldBeFalse();
        // A cancel while a rollback holds the record is kept for the record that rollback writes back
        (await env.Store.RequestCancelAsync("1")).ShouldBe(TaskCancelRequestResult.Requested);
        env.Record("1")!.CancelPending.ShouldBeTrue();
        // A rollback that stopped half-way is claimed again; a finished one is not
        (await env.Store.ClaimRollbackAsync("1")).ShouldNotBeNull();
        await env.Store.MarkRolledBackAsync("1");
        (await env.Store.RequestCancelAsync("1")).ShouldBe(TaskCancelRequestResult.RolledBack);
        (await env.Store.ClaimRollbackAsync("1")).ShouldBeNull();
    }

    [Test]
    public async Task EveryClaimChangesTheRecord_SoAClaimThatWritesNothingIsNoClaim()
    {
        using var env = new WriteRecordEnv();
        env.SeedTask("1", "Failed", new TaskWriteRecord().ToJson());

        var first = await env.Store.ClaimRollbackAsync("1");
        var stored = env.RecordJson("1");
        var again = await env.Store.ClaimRollbackAsync("1");

        first!.Claim.ShouldNotBeNull();
        again!.Claim.ShouldNotBe(first.Claim, "a claim writes a new claim id: it is never an identical write");
        env.RecordJson("1").ShouldNotBe(stored);
    }

    [Test]
    public async Task AClaimRacingAnotherClaim_DoesNotTakeTheRecordTheOtherClaimed()
    {
        var competing = new CompetingWriteInterceptor();
        using var env = new WriteRecordEnv(competing);
        env.SeedTask("1", "Failed", new TaskWriteRecord().ToJson());
        // Another rollback claims the record between this one's read and its compare-and-swap
        competing.Competitor = async () =>
        {
            await using var other = env.NewContext();
            (await WriteRecordEnv.NewStore(other).ClaimRollbackAsync("1")).ShouldNotBeNull();
        };

        (await env.Store.ClaimRollbackAsync("1")).ShouldBeNull("the other rollback holds the record");
        env.Record("1")!.State.ShouldBe(TaskWriteState.RollingBack);
    }

    [Test]
    public async Task ACommittedRecord_IsNeverClaimed()
    {
        using var env = new WriteRecordEnv();
        env.SeedTask("1", "Processing");

        (await env.Store.MarkCommittedAsync("1")).ShouldBeTrue();

        (await env.Store.ClaimRollbackAsync("1")).ShouldBeNull();
        (await env.Store.RequestCancelAsync("1")).ShouldBe(TaskCancelRequestResult.Committed);
    }

    [Test]
    public async Task ATaskWithoutRecord_HasNothingToCancel()
    {
        using var env = new WriteRecordEnv();
        env.SeedTask("1", "Processing");

        (await env.Store.RequestCancelAsync("1")).ShouldBe(TaskCancelRequestResult.NoRecord);
        env.RecordJson("1").ShouldBeNull();
    }

    [Test]
    public async Task TheSweepFindsOnlyEndedTasksWhoseRollbackIsUnfinished()
    {
        using var env = new WriteRecordEnv();
        var open = new TaskWriteRecord().ToJson();
        env.SeedTask("failed-open", "Failed", open);
        env.SeedTask("deleted-cancel", "Deleted", new TaskWriteRecord { State = TaskWriteState.CancelRequested }.ToJson());
        env.SeedTask("deleted-rolling", "Deleted", new TaskWriteRecord { State = TaskWriteState.RollingBack }.ToJson());
        env.SeedTask("deleted-done", "Deleted", new TaskWriteRecord { State = TaskWriteState.RolledBack }.ToJson());
        env.SeedTask("failed-committed", "Failed", new TaskWriteRecord { State = TaskWriteState.Committed }.ToJson());
        env.SeedTask("succeeded", "Succeeded", open);
        env.SeedTask("running", "Processing", open);
        env.SeedTask("none", "Failed");

        (await env.Store.EndedWithOpenRecordAsync())
            .ShouldBe(["failed-open", "deleted-cancel", "deleted-rolling"], ignoreOrder: true);
    }

    [Test]
    public async Task HeldPaths_AreThoseOfUnfinishedRecordsOfTheDataset()
    {
        using var env = new WriteRecordEnv();
        var other = new DatasetKey(WriteRecordEnv.Org, Guid.NewGuid());
        TaskWriteRecord With(TaskWriteState state, DatasetKey dataset, params string[] paths)
        {
            var record = new TaskWriteRecord { State = state };
            record.Add(dataset, paths, []);
            return record;
        }

        env.SeedTask("running", "Processing", With(TaskWriteState.Open, Dataset, "a.tif").ToJson());
        env.SeedTask("rolling", "Failed", With(TaskWriteState.RollingBack, Dataset, "b.tif").ToJson());
        env.SeedTask("done", "Deleted", With(TaskWriteState.RolledBack, Dataset, "c.tif").ToJson());
        env.SeedTask("committed", "Processing", With(TaskWriteState.Committed, Dataset, "d.tif").ToJson());
        env.SeedTask("succeeded", "Succeeded", With(TaskWriteState.Open, Dataset, "e.tif").ToJson());
        env.SeedTask("other", "Processing", With(TaskWriteState.Open, other, "f.tif").ToJson());

        (await env.Store.HeldPathsAsync(Dataset)).ShouldBe(["a.tif", "b.tif"], ignoreOrder: true);
    }

    [Test]
    public async Task TheRowOfAnotherJobWithTheSameId_GetsThePayloadRecord()
    {
        using var env = new WriteRecordEnv();
        var old = new TaskWriteRecord();
        old.Add(Dataset, ["old.tif"], []);
        env.SeedTask("7", "Failed", old.ToJson());
        var created = TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset(WriteRecordEnv.Org, Guid.NewGuid(), "n"))
            .ToJson();
        var writer = PublicationEnv.NewWriter(env.Db);

        (await writer.PrepareForPublicationAsync("7",
                new IndexPayload(WriteRecordEnv.Org, "ds", null, "u", WriteRecordJson: created),
                DateTime.UtcNow.AddMinutes(1), null))
            .ShouldBe(JobIndexPreparationResult.Replaced);
        env.RecordJson("7").ShouldBe(created);

        (await writer.PrepareForPublicationAsync("8", new IndexPayload(WriteRecordEnv.Org, "ds", null, "u"),
            DateTime.UtcNow, null)).ShouldBe(JobIndexPreparationResult.Inserted);
        env.RecordJson("8").ShouldBeNull();
    }

    [Test]
    public void ARequeue_StartsAnEmptyRecord_KeepingTheCreatedDataset_AndKeepsACommittedOrUnfinishedOne()
    {
        var created = new TaskCreatedDataset(WriteRecordEnv.Org, Guid.NewGuid(), "n");
        var rolledBack = TaskWriteRecord.ForCreatedDataset(created);
        rolledBack.State = TaskWriteState.RolledBack;
        rolledBack.Attempt = 3;
        rolledBack.Add(Dataset, ["a.tif"], []);

        TaskWriteRecord.ForRequeue(rolledBack)!.ToJson().ShouldBe(TaskWriteRecord.ForCreatedDataset(created).ToJson());
        TaskWriteRecord.ForRequeue(new TaskWriteRecord { State = TaskWriteState.RolledBack })!.ToJson()
            .ShouldBe(new TaskWriteRecord().ToJson());
        TaskWriteRecord.ForRequeue(null).ShouldBeNull("a task without a record (no dataset files, or from before " +
                                                      "the upgrade) stays without one");
        // The attempt rule of the new run deletes what is left before it writes (TaskRollback.StartAttemptAsync)
        TaskWriteRecord.ForRequeue(new TaskWriteRecord { State = TaskWriteState.RollingBack }).ShouldBeNull();
        TaskWriteRecord.ForRequeue(new TaskWriteRecord { State = TaskWriteState.Committed })
            .ShouldBeNull("a committed task is never retried (D-037)");
    }

    [Test]
    public async Task TheRowResetOfARequeue_LeavesTheWriteRecordAlone()
    {
        using var env = new WriteRecordEnv();
        var record = TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset(WriteRecordEnv.Org, Guid.NewGuid(), "n"));
        record.Add(Dataset, ["written-by-the-new-run.tif"], []);
        env.SeedTask("1", "Enqueued", record.ToJson());

        await PublicationEnv.NewWriter(env.Db).ResetForRequeueAsync("1", DateTime.UtcNow);

        // The retry resets the record by compare-and-swap before the re-queue (TaskRetry): a run that started at once
        // keeps what it recorded
        env.RecordJson("1").ShouldBe(record.ToJson());
    }

    // A re-run of the task (a Retry, a lost run run again) reads the dataset an earlier run created from the record, and
    // reuses it instead of creating a second one (PhotogrammetryTool, D-031)
    [Test]
    public async Task TheRecorder_ReadsTheCreatedDatasetOfAnEarlierRun_AfterARetry()
    {
        using var env = new WriteRecordEnv();
        var created = new TaskCreatedDataset(WriteRecordEnv.Org, Guid.NewGuid(), "brighton-fast");
        var failed = TaskWriteRecord.ForCreatedDataset(created);
        failed.State = TaskWriteState.RolledBack;
        failed.Add(Dataset, ["odm_orthophoto/odm_orthophoto.tif"], []);
        env.SeedTask("1", "Failed", failed.ToJson());
        env.SeedTask("2", "Processing");

        // What TaskRetry writes before the re-queue
        await env.Store.ChangeAsync("1", TaskWriteRecord.ForRequeue);

        using var recorder = Registry.Web.Services.HeavyTasks.Adapters.TaskWriteRecorder.For(env.Store, "1", Dataset,
            "photogrammetry");
        (await recorder.RecordedCreatedDatasetAsync()).ShouldBe(created);

        using var fresh = Registry.Web.Services.HeavyTasks.Adapters.TaskWriteRecorder.For(env.Store, "2", Dataset,
            "photogrammetry");
        (await fresh.RecordedCreatedDatasetAsync()).ShouldBeNull("a first run has created nothing");
    }

    [Test]
    public async Task TheEndedTasksWhoseCreatedDatasetDeleteWaits_AreFound()
    {
        using var env = new WriteRecordEnv();
        TaskWriteRecord With(TaskWriteState state, string? job) => new()
        {
            State = state, CreatedDatasetDeleteJob = job,
            CreatedDataset = new TaskCreatedDataset(WriteRecordEnv.Org, Guid.NewGuid(), "n")
        };

        env.SeedTask("waits", "Deleted", With(TaskWriteState.RolledBack, "job-1").ToJson());
        env.SeedTask("failed", "Failed", With(TaskWriteState.RolledBack, "job-2").ToJson());
        env.SeedTask("decided", "Failed", With(TaskWriteState.RolledBack, null).ToJson());
        env.SeedTask("rolling", "Failed", With(TaskWriteState.RollingBack, "job-3").ToJson());
        env.SeedTask("running", "Processing", With(TaskWriteState.RolledBack, "job-4").ToJson());

        (await env.Store.EndedWithCreatedDatasetDeleteAsync()).ShouldBe(["waits", "failed"], ignoreOrder: true);
    }
}

/// <summary>
/// The one rollback (FR-XC-27, D-004, D-045): it deletes only what the record holds, the empty folders it created, and a
/// dataset the task created (by folder id, by a delayed system delete, when nothing else is in it); a committed task is
/// never rolled back, and a rollback can run twice.
/// </summary>
[TestFixture]
public class TaskRollbackTests
{
    private WriteRecordEnv _env = null!;
    private string _root = null!;
    private readonly Dictionary<Guid, FakeDataset> _datasets = [];
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private readonly List<(string TaskId, Guid InternalRef, string? UserId)> _scheduledDeletes = [];

    [SetUp]
    public void SetUp()
    {
        _env = new WriteRecordEnv();
        _root = Path.Combine(Path.GetTempPath(), "taskrollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _datasets.Clear();
        _scheduledDeletes.Clear();
        _jobs = new Mock<IBackgroundJobsProcessor>();
        _jobs.Setup(j => j.Schedule(It.IsAny<Expression<Func<CreatedDatasetRollbackJob, Task>>>(), It.IsAny<TimeSpan>()))
            .Returns<Expression<Func<CreatedDatasetRollbackJob, Task>>, TimeSpan>((call, delay) =>
            {
                delay.ShouldBe(TaskRollback.CreatedDatasetDeleteDelay);
                var args = ((MethodCallExpression)call.Body).Arguments
                    .Select(a => Expression.Lambda(a).Compile().DynamicInvoke()).ToArray();
                _scheduledDeletes.Add(((string)args[0]!, (Guid)args[1]!, (string?)args[2]));
                return "delete-job";
            });
    }

    [TearDown]
    public void TearDown()
    {
        _env.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>A dataset folder on disk with a fake index.</summary>
    private sealed class FakeDataset
    {
        public FakeDataset(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
            Ddb = new Mock<IDDB>();
            Ddb.SetupGet(d => d.DatasetFolderPath).Returns(root);
            Ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(p => Path.Combine(root, p));
            Ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns<string>(Indexed.ContainsKey);
            Ddb.Setup(d => d.Remove(It.IsAny<string>())).Callback<string>(p =>
            {
                foreach (var key in Indexed.Keys.Where(k => k == p || k.StartsWith(p + "/", StringComparison.Ordinal)).ToList())
                    Indexed.Remove(key);
            });
            Ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns<EntryQuery>(q => [
                .. Indexed.Where(e => string.IsNullOrEmpty(q.Path)
                        ? q.Recursive || !e.Key.Contains('/')
                        : e.Key == q.Path || e.Key.StartsWith(q.Path + "/", StringComparison.Ordinal))
                    .Select(e => new EntrySummary { Path = e.Key, Type = e.Value })
            ]);
        }

        public string Root { get; }
        public Mock<IDDB> Ddb { get; }
        public Dictionary<string, EntryType> Indexed { get; } = [];

        public void Write(string path, bool indexed = true)
        {
            var local = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, path);
            if (indexed)
                Indexed[path] = EntryType.Generic;
        }

        public bool Exists(string path) => File.Exists(Path.Combine(Root, path)) || Directory.Exists(Path.Combine(Root, path));
    }

    private FakeDataset NewDataset(string slug, out Guid internalRef)
    {
        internalRef = _env.AddDataset(slug);
        var dataset = new FakeDataset(Path.Combine(_root, internalRef.ToString()));
        _datasets[internalRef] = dataset;
        return dataset;
    }

    private TaskRollback Rollback(IDatasetCacheInvalidator? caches = null)
    {
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns<string, Guid>((_, id) => _datasets[id].Ddb.Object);
        return new TaskRollback(_env.Store, _env.Db, ddbManager.Object, _jobs.Object,
            new TaskEntryChanges(caches ?? Mock.Of<IDatasetCacheInvalidator>(), _env.Db,
                NullLogger<TaskEntryChanges>.Instance), NullLogger<TaskRollback>.Instance);
    }

    private static string RecordOf(DatasetKey dataset, IEnumerable<string> paths, IEnumerable<string>? folders = null,
        TaskWriteState state = TaskWriteState.Open, TaskCreatedDataset? created = null, int attempt = 0)
    {
        var record = new TaskWriteRecord { State = state, CreatedDataset = created, Attempt = attempt };
        record.Add(dataset, paths, folders ?? []);
        return record.ToJson();
    }

    [Test]
    public async Task OnlyTheRecordedPaths_Go_AndAnUploadOfTheSameTimeStays()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("import/a.tif");
        ds.Write("import/deep/b.tif");
        ds.Write("import/upload.jpg");   // a user upload during the import: not recorded
        ds.Write("existing.tif");        // there before the task
        var caches = new Mock<IDatasetCacheInvalidator>();
        _env.SeedTask("1", "Failed", RecordOf(new DatasetKey(WriteRecordEnv.Org, id),
            ["import/a.tif", "import/deep/b.tif", "import/never-written.tif"], ["import", "import/deep"]));

        (await Rollback(caches.Object).RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("import/a.tif").ShouldBeFalse();
        ds.Exists("import/deep/b.tif").ShouldBeFalse();
        ds.Exists("import/deep").ShouldBeFalse("an empty folder the task created goes");
        ds.Exists("import/upload.jpg").ShouldBeTrue();
        ds.Exists("import").ShouldBeTrue("a folder that holds a file of another writer stays");
        ds.Exists("existing.tif").ShouldBeTrue();
        ds.Indexed.Keys.ShouldBe(["import/upload.jpg", "existing.tif"], ignoreOrder: true);
        _env.Record("1")!.State.ShouldBe(TaskWriteState.RolledBack);
        caches.Verify(c => c.InvalidateAllDatasetCachesAsync(WriteRecordEnv.Org, "ds"), Times.Once);
        _scheduledDeletes.ShouldBeEmpty();
    }

    [Test]
    public async Task ARollback_RunsTwiceSafely()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("a.tif");
        _env.SeedTask("1", "Deleted", RecordOf(new DatasetKey(WriteRecordEnv.Org, id), ["a.tif"]));

        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);
        ds.Write("a.tif");   // the same name, written again by somebody else after the rollback
        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.Nothing);

        ds.Exists("a.tif").ShouldBeTrue();
    }

    [Test]
    public async Task AnInterruptedRollback_IsResumed()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("b.tif");
        _env.SeedTask("1", "Failed", RecordOf(new DatasetKey(WriteRecordEnv.Org, id), ["a.tif", "b.tif"],
            state: TaskWriteState.RollingBack));

        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("b.tif").ShouldBeFalse();
    }

    [Test]
    public async Task ACommittedTask_IsNeverRolledBack()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("merged.tif");
        _env.SeedTask("1", "Failed", RecordOf(new DatasetKey(WriteRecordEnv.Org, id), ["merged.tif"],
            state: TaskWriteState.Committed));

        (await Rollback().RollBackAsync("1",
                [new PendingWrite(new DatasetKey(WriteRecordEnv.Org, id), "merged.tif", false)]))
            .ShouldBe(TaskRollbackOutcome.Committed);

        ds.Exists("merged.tif").ShouldBeTrue();
    }

    [Test]
    public async Task ThePathsTheWorkerHadNotWrittenToTheRecord_GoToo()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("recorded.tif");
        ds.Write("pending.tif", indexed: false);
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        _env.SeedTask("1", "Processing", RecordOf(key, ["recorded.tif"]));

        await Rollback().RollBackAsync("1", [new PendingWrite(key, "pending.tif", false)]);

        ds.Exists("recorded.tif").ShouldBeFalse();
        ds.Exists("pending.tif").ShouldBeFalse();
    }

    [Test]
    public async Task TheCreatedDataset_IsDeletedLater_ByItsFolderId_EvenAfterARenameAndASlugReuse()
    {
        NewDataset("new-ds", out var created);
        _env.SeedTask("1", "Deleted",
            TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset(WriteRecordEnv.Org, created, "new-ds")).ToJson(),
            ds: "new-ds", userId: "alice");
        // Renamed meanwhile, and its old slug taken by another dataset
        var renamed = _env.Db.Datasets.Single(d => d.InternalRef == created);
        renamed.Slug = "renamed";
        _env.Db.SaveChanges();
        NewDataset("new-ds", out var impostor);

        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        _scheduledDeletes.ShouldBe([("1", created, "alice")]);
        _env.Db.ChangeTracker.Clear();

        // The delayed delete: the system removes it without the access check of a user
        var listings = new Mock<IDatasetListingInvalidator>();
        var job = new CreatedDatasetRollbackJob(_env.Store, _env.Db, DdbManager(), new DatasetRemoval(_env.Db,
            listings.Object, _jobs.Object, NullLogger<DatasetRemoval>.Instance), _jobs.Object,
            NullLogger<CreatedDatasetRollbackJob>.Instance);
        await job.DeleteAsync("1", created, "alice");

        _env.Db.ChangeTracker.Clear();
        _env.Db.Datasets.Any(d => d.InternalRef == created).ShouldBeFalse();
        _env.Db.Datasets.Single(d => d.InternalRef == impostor).Slug.ShouldBe("new-ds");
        listings.Verify(l => l.ListingChangedAsync(WriteRecordEnv.Org, created, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task TheCreatedDataset_IsKept_WhenItHoldsOtherEntries()
    {
        var ds = NewDataset("new-ds", out var created);
        ds.Write("uploaded-by-the-user.jpg");
        ds.Write("imported.tif");
        _env.SeedTask("1", "Failed", RecordOf(new DatasetKey(WriteRecordEnv.Org, created), ["imported.tif"],
            created: new TaskCreatedDataset(WriteRecordEnv.Org, created, "new-ds")));

        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        _scheduledDeletes.ShouldBeEmpty();
        ds.Exists("imported.tif").ShouldBeFalse();
        ds.Exists("uploaded-by-the-user.jpg").ShouldBeTrue();
    }

    [Test]
    public async Task TheDelayedDelete_KeepsTheDataset_WhenTheTaskRunsAgain_OrItGotEntries()
    {
        var ds = NewDataset("new-ds", out var created);
        var createdRef = new TaskCreatedDataset(WriteRecordEnv.Org, created, "new-ds");
        // Retried by the user: the record starts again (open) with the dataset
        _env.SeedTask("1", "Enqueued", TaskWriteRecord.ForCreatedDataset(createdRef).ToJson());
        var job = new CreatedDatasetRollbackJob(_env.Store, _env.Db, DdbManager(), new DatasetRemoval(_env.Db,
            Mock.Of<IDatasetListingInvalidator>(), _jobs.Object, NullLogger<DatasetRemoval>.Instance),
            _jobs.Object, NullLogger<CreatedDatasetRollbackJob>.Instance);

        await job.DeleteAsync("1", created, null);
        _env.Db.Datasets.Any(d => d.InternalRef == created).ShouldBeTrue();

        // Rolled back, but somebody uploaded a file in the five minutes
        await _env.Store.ChangeAsync("1", r =>
        {
            r!.State = TaskWriteState.RolledBack;
            r.CreatedDatasetDeleteJob = "delete-job";
            return r;
        });
        SetTaskState("1", "Failed");
        ds.Write("late-upload.jpg");
        await job.DeleteAsync("1", created, null);
        _env.Db.Datasets.Any(d => d.InternalRef == created).ShouldBeTrue();
        _env.Record("1")!.CreatedDatasetDeleteJob.ShouldBeNull("the job decided: the startup sweep leaves it alone");
    }

    [Test]
    public async Task TheDelayedDelete_KeepsTheDataset_OfATaskQueuedAgainBeforeItsRollbackFinished()
    {
        NewDataset("new-ds", out var created);
        var record = TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset(WriteRecordEnv.Org, created, "new-ds"));
        record.State = TaskWriteState.RollingBack;
        record.CreatedDatasetDeleteJob = "delete-job";
        // Retried by the user: the record whose rollback did not finish stays rolling back until the run starts
        _env.SeedTask("1", "Enqueued", record.ToJson(), ds: "new-ds");

        await DeleteJob().DeleteAsync("1", created, null);

        _env.Db.ChangeTracker.Clear();
        _env.Db.Datasets.Any(d => d.InternalRef == created).ShouldBeTrue();
    }

    [Test]
    public async Task AFileTheUserReplaced_IsKept_AndOneWithoutStampOrWithTheSameStamp_Goes()
    {
        var ds = NewDataset("ds", out var id);
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        ds.Write("replaced.tif");
        ds.Write("same.tif");
        ds.Write("unstamped.tif");
        var record = new TaskWriteRecord();
        record.Add(key, ["unstamped.tif"], [], new Dictionary<string, string>
        {
            ["replaced.tif"] = TaskWriteRecord.StampOf(Path.Combine(ds.Root, "replaced.tif"))!,
            ["same.tif"] = TaskWriteRecord.StampOf(Path.Combine(ds.Root, "same.tif"))!
        });
        _env.SeedTask("1", "Failed", record.ToJson());
        // An upload with overwrite replaced it after the task wrote it
        File.WriteAllText(Path.Combine(ds.Root, "replaced.tif"), "the user's own, longer content");

        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("replaced.tif").ShouldBeTrue();
        ds.Indexed.Keys.ShouldContain("replaced.tif");
        File.ReadAllText(Path.Combine(ds.Root, "replaced.tif")).ShouldBe("the user's own, longer content");
        ds.Exists("same.tif").ShouldBeFalse();
        ds.Exists("unstamped.tif").ShouldBeFalse("a path without stamp is deleted: the rollback deletes what it recorded");
    }

    [Test]
    public async Task ThePathsTheWorkerHadNotWritten_GoIntoTheRecordBeforeTheyAreDeleted()
    {
        var ds = NewDataset("ds", out var id);
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        ds.Write("pending.tif", indexed: false);
        _env.SeedTask("1", "Deleted", new TaskWriteRecord { State = TaskWriteState.CancelRequested }.ToJson());

        (await Rollback().RollBackAsync("1", [new PendingWrite(key, "pending.tif", false)]))
            .ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("pending.tif").ShouldBeFalse();
        var record = _env.Record("1")!;
        record.State.ShouldBe(TaskWriteState.RolledBack);
        record.Datasets.Single().Paths.ShouldBe(["pending.tif"]);
    }

    [Test]
    public async Task AnImportFromBeforeTheUpgrade_WithoutRecord_DeletesTheDatasetItCreated_WhenItIsEmpty()
    {
        NewDataset("old-import", out var created);
        var full = NewDataset("old-import-with-files", out var kept);
        full.Write("a.tif");
        NewDataset("existing", out _);
        // Running at the upgrade, ended when its job was lost: no write record (§8.3)
        _env.SeedTask("import", "Deleted", ds: "old-import", userId: "alice");
        _env.SeedTask("import-with-files", "Failed", ds: "old-import-with-files");
        _env.SeedTask("other-tool", "Deleted", ds: "existing");
        SetTool("other-tool", "archive-extract");

        (await Rollback().RollBackAsync("import")).ShouldBe(TaskRollbackOutcome.RolledBack);
        (await Rollback().RollBackAsync("import-with-files")).ShouldBe(TaskRollbackOutcome.RolledBack);
        (await Rollback().RollBackAsync("other-tool")).ShouldBe(TaskRollbackOutcome.Nothing);

        _scheduledDeletes.ShouldBe([("import", created, "alice")]);
        var record = _env.Record("import")!;
        record.State.ShouldBe(TaskWriteState.RolledBack);
        record.CreatedDataset.ShouldBe(new TaskCreatedDataset(WriteRecordEnv.Org, created, "old-import"));
        record.CreatedDatasetDeleteJob.ShouldBe("delete-job");
        _env.Record("import-with-files")!.CreatedDataset!.InternalRef.ShouldBe(kept);
        _env.RecordJson("other-tool").ShouldBeNull();

        _env.Db.ChangeTracker.Clear();
        await DeleteJob().DeleteAsync("import", created, "alice");
        _env.Db.ChangeTracker.Clear();
        _env.Db.Datasets.Any(d => d.InternalRef == created).ShouldBeFalse();
        _env.Db.Datasets.Any(d => d.InternalRef == kept).ShouldBeTrue();
    }

    [Test]
    public async Task AnImportFromBeforeTheUpgrade_RunAgain_RecordsTheDatasetItCreated()
    {
        NewDataset("old-import", out var created);
        NewDataset("existing", out _);
        _env.SeedTask("import", "Processing", ds: "old-import");
        _env.SeedTask("other-tool", "Processing", ds: "existing");
        SetTool("other-tool", "file-url-import");

        (await Rollback().StartAttemptAsync("import", 0)).ShouldBe(TaskAttemptStart.Fresh);
        (await Rollback().StartAttemptAsync("other-tool", 0)).ShouldBe(TaskAttemptStart.Fresh);

        _env.Record("import")!.CreatedDataset.ShouldBe(new TaskCreatedDataset(WriteRecordEnv.Org, created, "old-import"));
        _env.Record("other-tool")!.CreatedDataset.ShouldBeNull();
    }

    [Test]
    public async Task TheStartupSweep_SchedulesAgainTheDeleteOfACreatedDataset_WhoseJobWasLost()
    {
        NewDataset("lost", out var lost);
        NewDataset("waiting", out var waiting);
        var full = NewDataset("full", out var withEntries);
        full.Write("uploaded-since.jpg");
        var gone = Guid.NewGuid();
        string RolledBack(Guid created, string slug, string job) => new TaskWriteRecord
        {
            State = TaskWriteState.RolledBack, CreatedDatasetDeleteJob = job,
            CreatedDataset = new TaskCreatedDataset(WriteRecordEnv.Org, created, slug)
        }.ToJson();
        _env.SeedTask("lost", "Deleted", RolledBack(lost, "lost", "lost-job"), ds: "lost");
        _env.SeedTask("waiting", "Failed", RolledBack(waiting, "waiting", "waiting-job"), ds: "waiting");
        _env.SeedTask("full", "Failed", RolledBack(withEntries, "full", "full-job"), ds: "full");
        _env.SeedTask("gone", "Failed", RolledBack(gone, "gone", "gone-job"), ds: "gone");
        _jobs.Setup(j => j.GetJobStatus("waiting-job")).Returns(JobStatus.Scheduled);

        await Rollback().SweepAsync();

        _scheduledDeletes.Select(d => d.InternalRef).ShouldBe([lost]);
        _env.Record("lost")!.CreatedDatasetDeleteJob.ShouldBe("delete-job");
        _env.Record("waiting")!.CreatedDatasetDeleteJob.ShouldBe("waiting-job");
        _env.Record("full")!.CreatedDatasetDeleteJob.ShouldBeNull("kept for good: it holds entries");
        _env.Record("gone")!.CreatedDatasetDeleteJob.ShouldBeNull("deleted already");
    }

    [Test]
    public async Task ARollbackThatRunsAgain_KeepsTheDeleteJobThatStillWaits()
    {
        var ds = NewDataset("new-ds", out var created);
        ds.Write("a.tif");
        var record = TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset(WriteRecordEnv.Org, created, "new-ds"));
        record.State = TaskWriteState.RollingBack;
        record.CreatedDatasetDeleteJob = "waiting-job";
        record.Add(new DatasetKey(WriteRecordEnv.Org, created), ["a.tif"], []);
        _env.SeedTask("1", "Failed", record.ToJson(), ds: "new-ds");
        _jobs.Setup(j => j.GetJobStatus("waiting-job")).Returns(JobStatus.Scheduled);

        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("a.tif").ShouldBeFalse();
        _scheduledDeletes.ShouldBeEmpty();
        _env.Record("1")!.CreatedDatasetDeleteJob.ShouldBe("waiting-job");
    }

    [Test]
    public async Task TheAttemptRule_StopsACommittedTask_AlsoOnALaterAttempt()
    {
        _env.SeedTask("1", "Processing", new TaskWriteRecord { State = TaskWriteState.Committed, Attempt = 0 }.ToJson());

        (await Rollback().StartAttemptAsync("1", 2)).ShouldBe(TaskAttemptStart.CommittedLost);

        _env.Record("1")!.State.ShouldBe(TaskWriteState.Committed);
    }

    private CreatedDatasetRollbackJob DeleteJob() =>
        new(_env.Store, _env.Db, DdbManager(), new DatasetRemoval(_env.Db, Mock.Of<IDatasetListingInvalidator>(),
            _jobs.Object, NullLogger<DatasetRemoval>.Instance), _jobs.Object,
            NullLogger<CreatedDatasetRollbackJob>.Instance);

    private void SetTaskState(string jobId, string state)
    {
        using var db = _env.NewContext();
        db.JobIndices.Single(j => j.JobId == jobId).CurrentState = state;
        db.SaveChanges();
    }

    private void SetTool(string jobId, string toolId)
    {
        using var db = _env.NewContext();
        db.JobIndices.Single(j => j.JobId == jobId).ToolId = toolId;
        db.SaveChanges();
    }

    [Test]
    public async Task TheAttemptRule_RollsBackALostRun_AndKeepsTheCreatedDataset()
    {
        var ds = NewDataset("new-ds", out var created);
        ds.Write("a.tif");
        var createdRef = new TaskCreatedDataset(WriteRecordEnv.Org, created, "new-ds");
        _env.SeedTask("1", "Processing", RecordOf(new DatasetKey(WriteRecordEnv.Org, created), ["a.tif"],
            created: createdRef, attempt: 1));

        // Run again with the same retry count: the run that recorded a.tif was lost
        (await Rollback().StartAttemptAsync("1", 1)).ShouldBe(TaskAttemptStart.LostRunRolledBack);

        ds.Exists("a.tif").ShouldBeFalse();
        var record = _env.Record("1")!;
        record.State.ShouldBe(TaskWriteState.Open);
        record.Attempt.ShouldBe(1);
        record.HasWrites.ShouldBeFalse();
        record.CreatedDataset.ShouldBe(createdRef);
        _scheduledDeletes.ShouldBeEmpty();
    }

    [Test]
    public async Task TheAttemptRule_KeepsTheRecordOfAnAutomaticRetry()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("a.tif");
        _env.SeedTask("1", "Processing", RecordOf(new DatasetKey(WriteRecordEnv.Org, id), ["a.tif"], attempt: 0));

        (await Rollback().StartAttemptAsync("1", 1)).ShouldBe(TaskAttemptStart.Retry);

        ds.Exists("a.tif").ShouldBeTrue();
        var record = _env.Record("1")!;
        record.Attempt.ShouldBe(1);
        record.Datasets.Single().Paths.ShouldBe(["a.tif"]);
    }

    [Test]
    public async Task TheAttemptRule_StartsARecord_AndStopsALostCommittedTask()
    {
        _env.SeedTask("1", "Processing");
        _env.SeedTask("2", "Processing", new TaskWriteRecord { State = TaskWriteState.Committed }.ToJson());

        (await Rollback().StartAttemptAsync("1", 0)).ShouldBe(TaskAttemptStart.Fresh);
        (await Rollback().StartAttemptAsync("2", 0)).ShouldBe(TaskAttemptStart.CommittedLost);

        _env.Record("1")!.State.ShouldBe(TaskWriteState.Open);
        _env.Record("2")!.State.ShouldBe(TaskWriteState.Committed);
    }

    [Test]
    public async Task TheStartupSweep_FinishesAnInterruptedRollback_AndACancelBeforeTheStart()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("half.tif");
        NewDataset("queued-import", out var created);
        _env.SeedTask("interrupted", "Failed", RecordOf(new DatasetKey(WriteRecordEnv.Org, id), ["half.tif"],
            state: TaskWriteState.RollingBack));
        _env.SeedTask("cancelled", "Deleted", new TaskWriteRecord
        {
            State = TaskWriteState.CancelRequested,
            CreatedDataset = new TaskCreatedDataset(WriteRecordEnv.Org, created, "queued-import")
        }.ToJson());
        ds.Write("kept.tif");
        _env.SeedTask("succeeded", "Succeeded", RecordOf(new DatasetKey(WriteRecordEnv.Org, id), ["kept.tif"]));

        (await Rollback().SweepAsync()).ShouldBe(2);

        ds.Exists("half.tif").ShouldBeFalse();
        ds.Exists("kept.tif").ShouldBeTrue();
        _scheduledDeletes.Select(d => d.InternalRef).ShouldBe([created]);
        _env.Record("interrupted")!.State.ShouldBe(TaskWriteState.RolledBack);
        _env.Record("cancelled")!.State.ShouldBe(TaskWriteState.RolledBack);
        _env.Record("succeeded")!.State.ShouldBe(TaskWriteState.Open);
    }

    // §8.1: a path is in the stored record before the first byte of it is written. The process is "lost" right after
    // the writer returns: nothing flushes the recorder, and the rollback reads only the stored record

    [Test]
    public async Task AFileTheRegistryImportMoved_IsRolledBack_WhenTheProcessIsLostRightAfter()
    {
        var ds = NewDataset("ds", out var id);
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        _env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        using var recorder = Registry.Web.Services.HeavyTasks.Adapters.TaskWriteRecorder.For(_env.Store, "1", key,
            HeavyToolIds.ImportDataset);
        var writes = await TaskImportWrites.StartAsync(recorder, key, ds.Root, CancellationToken.None);
        var client = new Registry.Web.Services.Import.RemoteRegistryClient(HttpFactory(new ServingHandler()),
            new Registry.Web.Models.Configuration.ImportSettings { RegistryDownloadMaxRetries = 1 }, TestPaths.Policy,
            TestScratch.Provider, NullLogger<Registry.Web.Services.Import.RemoteRegistryClient>.Instance);

        await client.DownloadFilesParallelAsync("http://remote.test", null, "src", "ds", ds.Root,
        [
            new Registry.Web.Models.DTO.EntryDto { Path = "a.jpg", Type = EntryType.Image, Size = 4 },
            new Registry.Web.Models.DTO.EntryDto { Path = "sub/b.jpg", Type = EntryType.Image, Size = 4 }
        ], writes, null, CancellationToken.None);
        ds.Exists("a.jpg").ShouldBeTrue();
        ds.Exists("sub/b.jpg").ShouldBeTrue();

        // Lost here (CloseLostAsync rolls the task back from its stored record)
        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("a.jpg").ShouldBeFalse();
        ds.Exists("sub/b.jpg").ShouldBeFalse();
        ds.Exists("sub").ShouldBeFalse("the folder the import created was in the record before it existed");
    }

    [Test]
    public async Task AnArchiveEntryExtracted_IsRolledBackByTheAttemptRule_WhenTheProcessIsLostRightAfter()
    {
        var ds = NewDataset("ds", out var id);
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        _env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        var archive = Path.Combine(_root, "in.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "a.txt", "deep/er/b.txt" })
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write("x"u8);
            }
        }

        using (var recorder = Registry.Web.Services.HeavyTasks.Adapters.TaskWriteRecorder.For(_env.Store, "1", key,
                   "archive-extract"))
        {
            var writes = await TaskImportWrites.StartAsync(recorder, key, ds.Root, CancellationToken.None);
            await TestArchives.Extractor(new Registry.Adapters.Archives.SharpCompressArchiveExtractor()).ExtractAsync(
                new ArchiveExtractionRequest(archive, ds.Root, string.Empty, _ => true, _ => false, 0, 0, writes),
                null, CancellationToken.None);
        }

        ds.Exists("a.txt").ShouldBeTrue();
        ds.Exists("deep/er/b.txt").ShouldBeTrue();

        // Lost here: the same attempt runs again, with a fresh recorder
        (await Rollback().StartAttemptAsync("1", 0)).ShouldBe(TaskAttemptStart.LostRunRolledBack);

        ds.Exists("a.txt").ShouldBeFalse();
        ds.Exists("deep/er/b.txt").ShouldBeFalse();
        ds.Exists("deep").ShouldBeFalse();
        _env.Record("1")!.HasWrites.ShouldBeFalse();
    }

    [Test]
    public async Task AFileAnOverwriteExtractionReplaced_StaysOutOfTheRecord_WhenALaterBatchHasItAgain()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("a.txt");   // there before the task: the extraction replaces it (overwrite)
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        _env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        var archive = Path.Combine(_root, "in.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            // The duplicate comes more than one batch after the first a.txt
            foreach (var (name, content) in new[] { ("a.txt", "first"), ("b.txt", "b"), ("c.txt", "c"), ("a.txt", "second") })
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
        }

        var extractor = new DatasetArchiveExtractor(new Registry.Adapters.Archives.SharpCompressArchiveExtractor(),
            TestPaths.Policy, TestScratch.Provider, new Registry.Adapters.FileSystem(), TestHeadroom.Default,
            NullLogger<DatasetArchiveExtractor>.Instance) { PlaceBatchSize = 1 };
        using (var recorder = Registry.Web.Services.HeavyTasks.Adapters.TaskWriteRecorder.For(_env.Store, "1", key,
                   "archive-extract"))
        {
            var writes = await TaskImportWrites.StartAsync(recorder, key, ds.Root, CancellationToken.None);
            await extractor.ExtractAsync(
                new ArchiveExtractionRequest(archive, ds.Root, string.Empty, _ => true, _ => false, 0, 0, writes),
                null, CancellationToken.None);
        }

        File.ReadAllText(Path.Combine(ds.Root, "a.txt")).ShouldBe("second");
        _env.Record("1")!.Datasets.Single().Paths.ShouldBe(["b.txt", "c.txt"], ignoreOrder: true);

        // Lost after the second move of a.txt: the rollback deletes only what the task created
        (await Rollback().RollBackAsync("1")).ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("a.txt").ShouldBeTrue("a file that existed before the task is never deleted");
        ds.Exists("b.txt").ShouldBeFalse();
        ds.Exists("c.txt").ShouldBeFalse();
    }

    // FR-XC-27: a partial output that libddb leaves when it fails is the task's own file, recorded before the native
    // call: the rollback deletes it, so the next attempt finds no name clash

    [Test]
    public async Task APartialAlignOutput_IsRolledBack_WhenLibddbFailsAfterCreatingIt()
    {
        var ds = NewDataset("ds", out var id);
        ds.Ddb.Setup(d => d.AlignRaster("src.tif", "ref.tif", "out/new.tif", It.IsAny<string>(), It.IsAny<bool>()))
            .Callback(() => ds.Write("out/new.tif", indexed: false))
            .Throws(new InvalidOperationException("Cannot align raster: the write failed"));
        var request = new Registry.Web.Services.HeavyTasks.Models.HeavyToolRequest("align-raster", "1", "org", "ds",
            null, System.Text.Json.JsonSerializer.SerializeToElement(new
                { sourcePath = "src.tif", referencePath = "ref.tif", outputPath = "out/new.tif" }));

        await RunFailingOutputToolAsync(ds, id, "align-raster",
            new Registry.Web.Services.HeavyTasks.Tools.AlignRasterTool(TestPaths.Policy), request);
    }

    [Test]
    public async Task APartialMergeOutput_IsRolledBack_WhenLibddbFailsAfterCreatingIt()
    {
        var ds = NewDataset("ds", out var id);
        ds.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), "out/new.tif", It.IsAny<bool>()))
            .Callback(() => ds.Write("out/new.tif", indexed: false))
            .Throws(new InvalidOperationException("Cannot merge multispectral: the write failed"));
        var request = new Registry.Web.Services.HeavyTasks.Models.HeavyToolRequest("merge-multispectral", "1", "org",
            "ds", null, System.Text.Json.JsonDocument.Parse(
                """{ "paths": ["b1.tif", "b2.tif"], "outputPath": "out/new.tif" }""").RootElement);

        await RunFailingOutputToolAsync(ds, id, "merge-multispectral",
            new Registry.Web.Services.HeavyTasks.Tools.MergeMultispectralTool(TestPaths.Policy), request);
    }

    private async Task RunFailingOutputToolAsync(FakeDataset ds, Guid id, string toolId,
        Registry.Web.Services.HeavyTasks.Ports.IHeavyTool tool,
        Registry.Web.Services.HeavyTasks.Models.HeavyToolRequest request)
    {
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        _env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        using var recorder = Registry.Web.Services.HeavyTasks.Adapters.TaskWriteRecorder.For(_env.Store, "1", key, toolId);
        var ctx = new Mock<Registry.Web.Services.HeavyTasks.Models.IHeavyToolExecutionContext>();
        ctx.SetupGet(c => c.Ddb).Returns(ds.Ddb.Object);
        ctx.SetupGet(c => c.TaskId).Returns("1");
        ctx.SetupGet(c => c.Dataset).Returns(key);
        ctx.SetupGet(c => c.WriteRecord).Returns(recorder);

        await Should.ThrowAsync<InvalidOperationException>(() => tool.ExecuteAsync(request, ctx.Object,
            new Progress<Registry.Web.Services.HeavyTasks.Models.HeavyToolProgress>(), CancellationToken.None));
        ds.Exists("out/new.tif").ShouldBeTrue("libddb left a partial output");

        (await Rollback().RollBackAsync("1", recorder.Pending)).ShouldBe(TaskRollbackOutcome.RolledBack);

        ds.Exists("out/new.tif").ShouldBeFalse();
        ds.Exists("out").ShouldBeFalse("the folder the task created for its output goes too");
    }

    [Test]
    public async Task ACancel_KeepsTheStampsTheWorkerHadNotWritten_SoAFileTheUserReplacedStays()
    {
        var ds = NewDataset("ds", out var id);
        var key = new DatasetKey(WriteRecordEnv.Org, id);
        _env.SeedTask("1", "Processing", new TaskWriteRecord().ToJson());
        using var recorder = Registry.Web.Services.HeavyTasks.Adapters.TaskWriteRecorder.For(_env.Store, "1", key, "w");
        recorder.Record("a.tif");
        recorder.Record("b.tif");
        await recorder.FlushAsync();
        ds.Write("a.tif", indexed: false);
        ds.Write("b.tif", indexed: false);
        recorder.Written(key, "a.tif", Path.Combine(ds.Root, "a.tif"));
        recorder.Written(key, "b.tif", Path.Combine(ds.Root, "b.tif"));
        // An upload with overwrite replaced it after the task wrote it, before the stamp was flushed
        File.WriteAllText(Path.Combine(ds.Root, "a.tif"), "the user's own, longer content");

        recorder.Pending.Where(p => p.Stamp != null).Select(p => p.Path).ShouldBe(["a.tif", "b.tif"]);
        (await Rollback().RollBackAsync("1", recorder.Pending)).ShouldBe(TaskRollbackOutcome.RolledBack);

        File.ReadAllText(Path.Combine(ds.Root, "a.tif")).ShouldBe("the user's own, longer content");
        ds.Exists("b.tif").ShouldBeFalse();
    }

    [Test]
    public async Task ACancelDuringTheCleanupOfALostRun_IsKept_SoTheRunCannotCommit()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("a.tif");
        _env.SeedTask("1", "Processing", RecordOf(new DatasetKey(WriteRecordEnv.Org, id), ["a.tif"], attempt: 1));
        // The user cancels while the cleanup holds the record (rollingBack)
        ds.Ddb.Setup(d => d.EntryExists("a.tif")).Returns(() =>
        {
            using var other = _env.NewContext();
            WriteRecordEnv.NewStore(other).RequestCancelAsync("1").GetAwaiter().GetResult()
                .ShouldBe(TaskCancelRequestResult.Requested);
            return true;
        });

        (await Rollback().StartAttemptAsync("1", 1)).ShouldBe(TaskAttemptStart.LostRunRolledBack);

        ds.Exists("a.tif").ShouldBeFalse();
        _env.Record("1")!.State.ShouldBe(TaskWriteState.CancelRequested);
        (await _env.Store.MarkCommittedAsync("1")).ShouldBeFalse("the run that starts now can no longer commit");
    }

    [Test]
    public async Task TheDelayedDelete_KeepsTheDataset_OfATaskWhoseJobRunsAgain_BeforeItsRowSaysSo()
    {
        NewDataset("new-ds", out var created);
        var record = TaskWriteRecord.ForCreatedDataset(new TaskCreatedDataset(WriteRecordEnv.Org, created, "new-ds"));
        record.State = TaskWriteState.RolledBack;
        _env.SeedTask("1", "Failed", record.ToJson(), ds: "new-ds");
        // Re-queued: the job is enqueued, its row still says Failed
        _jobs.Setup(j => j.GetJobStatus("1")).Returns(JobStatus.Enqueued);

        await DeleteJob().DeleteAsync("1", created, null);

        _env.Db.ChangeTracker.Clear();
        _env.Db.Datasets.Any(d => d.InternalRef == created).ShouldBeTrue();
    }

    [Test]
    public async Task ACommittedTaskLostWithTheJobStorage_EndsFailed_NamingTheSourcesThatRemain()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("bands/nir.tif");
        ds.Write("merged.tif");
        var record = new TaskWriteRecord
        {
            State = TaskWriteState.Committed,
            CommittedSources = new TaskCommittedSources(WriteRecordEnv.Org, id, ["bands/red.tif", "bands/nir.tif"])
        };
        record.Add(new DatasetKey(WriteRecordEnv.Org, id), ["merged.tif"], []);
        _env.SeedTask("1", "Processing", record.ToJson());
        // The InMemory storage after a restart: the job is gone
        var sync = new JobIndexSyncService(new JobIndexQuery(_env.Db), PublicationEnv.NewWriter(_env.Db),
            new Hangfire.InMemory.InMemoryStorage(
                Registry.Web.Hosting.RegistryJobsServiceCollectionExtensions.InMemoryStorageOptions),
            NullLogger<JobIndexSyncService>.Instance, Rollback());

        (await sync.CloseLostAsync(DateTime.UtcNow.AddMinutes(1))).ShouldBe(1);

        using var db = _env.NewContext();
        var row = db.JobIndices.AsNoTracking().Single(j => j.JobId == "1");
        row.CurrentState.ShouldBe("Failed");
        row.ErrorType.ShouldBe(nameof(TaskCommittedLostException));
        // The worker's message for the same task (HeavyTaskJobWrapper, on a re-run)
        row.ErrorMessage.ShouldBe(TaskCommittedLostException
            .For(["bands/red.tif", "bands/nir.tif"], p => p == "bands/nir.tif").Message);
        row.ErrorMessage.ShouldContain("'bands/nir.tif'");
        ds.Exists("merged.tif").ShouldBeTrue("the output of a committed task stays");
        _env.Record("1")!.State.ShouldBe(TaskWriteState.Committed);
    }

    [Test]
    public async Task ACommittedLostTask_InterruptedWhileItEnds_IsEndedAtTheNextStart_NeverLeftDeletedOrWithoutItsMessage()
    {
        var ds = NewDataset("ds", out var id);
        ds.Write("merged.tif");
        var record = new TaskWriteRecord { State = TaskWriteState.Committed };
        record.Add(new DatasetKey(WriteRecordEnv.Org, id), ["merged.tif"], []);
        _env.SeedTask("1", "Processing", record.ToJson());
        var storage = new Hangfire.InMemory.InMemoryStorage(
            Registry.Web.Hosting.RegistryJobsServiceCollectionExtensions.InMemoryStorageOptions);

        // The process is lost at the second write of the row, after the first one is done
        var real = PublicationEnv.NewWriter(_env.Db);
        var writes = 0;
        void CrashAtTheSecondWrite()
        {
            if (++writes == 2)
                throw new InvalidOperationException("the process is lost");
        }

        var crashing = new Mock<IJobIndexWriter>();
        crashing.Setup(w => w.UpdateErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, string?, CancellationToken>((job, type, message, ct) =>
            {
                CrashAtTheSecondWrite();
                return real.UpdateErrorAsync(job, type, message, ct);
            });
        crashing.Setup(w => w.UpdateStateIfCurrentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<string, IReadOnlyCollection<string>, string, DateTime, CancellationToken>((job, from, to, at, ct) =>
            {
                CrashAtTheSecondWrite();
                return real.UpdateStateIfCurrentAsync(job, from, to, at, ct);
            });
        var lost = new JobIndexSyncService(new JobIndexQuery(_env.Db), crashing.Object, storage,
            NullLogger<JobIndexSyncService>.Instance, Rollback());

        await Should.ThrowAsync<InvalidOperationException>(() => lost.CloseLostAsync(DateTime.UtcNow.AddMinutes(1)));

        using (var db = _env.NewContext())
            db.JobIndices.AsNoTracking().Single(j => j.JobId == "1").CurrentState
                .ShouldBe("Processing", "still active: the next start ends it");

        // The next start
        var sync = new JobIndexSyncService(new JobIndexQuery(_env.Db), PublicationEnv.NewWriter(_env.Db), storage,
            NullLogger<JobIndexSyncService>.Instance, Rollback());
        (await sync.CloseLostAsync(DateTime.UtcNow.AddMinutes(1))).ShouldBe(1);

        using (var db = _env.NewContext())
        {
            var row = db.JobIndices.AsNoTracking().Single(j => j.JobId == "1");
            row.CurrentState.ShouldBe("Failed");
            row.ErrorType.ShouldBe(nameof(TaskCommittedLostException));
            row.ErrorMessage.ShouldNotBeNullOrEmpty();
        }

        ds.Exists("merged.tif").ShouldBeTrue("the output of a committed task stays");
        _env.Record("1")!.State.ShouldBe(TaskWriteState.Committed);
    }

    [Test]
    public async Task ACommittedLostTask_EndsFailedWithOneStateChange_ItsMessageWrittenBefore()
    {
        var ds = NewDataset("ds", out var id);
        var record = new TaskWriteRecord { State = TaskWriteState.Committed };
        record.Add(new DatasetKey(WriteRecordEnv.Org, id), ["merged.tif"], []);
        _env.SeedTask("1", "Processing", record.ToJson());
        var real = PublicationEnv.NewWriter(_env.Db);
        var calls = new List<string>();
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(w => w.UpdateErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, string?, CancellationToken>((job, type, message, ct) =>
            {
                calls.Add("error " + type);
                return real.UpdateErrorAsync(job, type, message, ct);
            });
        writer.Setup(w => w.UpdateStateIfCurrentAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<string, IReadOnlyCollection<string>, string, DateTime, CancellationToken>((job, from, to, at, ct) =>
            {
                calls.Add($"{string.Join(",", from)} -> {to}");
                return real.UpdateStateIfCurrentAsync(job, from, to, at, ct);
            });
        var sync = new JobIndexSyncService(new JobIndexQuery(_env.Db), writer.Object,
            new Hangfire.InMemory.InMemoryStorage(
                Registry.Web.Hosting.RegistryJobsServiceCollectionExtensions.InMemoryStorageOptions),
            NullLogger<JobIndexSyncService>.Instance, Rollback());

        (await sync.CloseLostAsync(DateTime.UtcNow.AddMinutes(1))).ShouldBe(1);

        calls.ShouldBe(["error " + nameof(TaskCommittedLostException), "Processing -> Failed"]);
    }

    private static IHttpClientFactoryForTests HttpFactory(System.Net.Http.HttpMessageHandler handler) => new(handler);

    private sealed class IHttpClientFactoryForTests(System.Net.Http.HttpMessageHandler handler)
        : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(handler, false);
    }

    // A remote registry that serves every file with a 4-byte body
    private sealed class ServingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken ct) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new System.Net.Http.ByteArrayContent("data"u8.ToArray()) });
    }

    private IDdbManager DdbManager()
    {
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns<string, Guid>((_, id) => _datasets[id].Ddb.Object);
        return ddbManager.Object;
    }
}

/// <summary>
/// The record from the submit to the task list: the dataset created for a task is in its first record, and a task that
/// ended or committed offers no Cancel.
/// </summary>
[TestFixture]
public class TaskWriteRecordPlumbingTests
{
    [Test]
    public void TheCreatedDataset_IsInTheFirstRecordOfTheTask()
    {
        IndexPayload? payload = null;
        var processor = new Mock<IBackgroundJobsProcessor>();
        processor.Setup(p => p.EnqueueIndexed(
                It.IsAny<Expression<Func<Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobWrapper, Task>>>(),
                It.IsAny<IndexPayload>()))
            .Callback<Expression<Func<Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobWrapper, Task>>, IndexPayload>(
                (_, meta) => payload = meta)
            .Returns("1");
        var created = new TaskCreatedDataset("org", Guid.NewGuid(), "new-ds");
        var request = new Registry.Web.Services.HeavyTasks.Models.HeavyToolRequest("import-dataset", "1", "org",
            "new-ds", null, System.Text.Json.JsonDocument.Parse("{}").RootElement);

        Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobs.EnqueueHeavyTask(processor.Object, request, "u",
            createdDataset: created);
        TaskWriteRecord.Parse(payload!.WriteRecordJson)!.CreatedDataset.ShouldBe(created);

        Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobs.EnqueueHeavyTask(processor.Object, request, "u");
        payload!.WriteRecordJson.ShouldBeNull();

        // A tool that adds dataset files is queued with a record: only a task from before the upgrade has none (§8.3)
        Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobs.EnqueueHeavyTask(processor.Object, request, "u",
            addsDatasetFiles: true);
        payload!.WriteRecordJson.ShouldBe(new TaskWriteRecord().ToJson());
    }

    [TestCase("Processing", null, true)]
    [TestCase("Enqueued", "{\"state\":\"open\"}", true)]
    [TestCase("Processing", "{\"state\":\"cancelRequested\"}", true)]
    [TestCase("Processing", "{\"state\":\"committed\"}", false)]
    [TestCase("Failed", null, false)]
    [TestCase("Succeeded", null, false)]
    [TestCase("Deleted", null, false)]
    public void ATaskIsCancellable_UntilItEndsOrCommits(string state, string? record, bool cancellable) =>
        Registry.Web.Services.PlatformTasks.TaskHistory.IsCancellable(new JobIndex
            { JobId = "1", CurrentState = state, WriteRecordJson = record }).ShouldBe(cancellable);
}
