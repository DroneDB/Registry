#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Forward-only JobIndex preparation and the conditional state update, on a relational provider
/// (SQLite, set-based paths) and on the EF in-memory provider (tracked fallback paths).
/// </summary>
[TestFixture]
[NonParallelizable]
public class JobIndexWriterPreparationTests
{
    private static readonly DateTime Created = new(2026, 5, 6, 7, 8, 9, 123, DateTimeKind.Utc);

    private static IndexPayload Payload(string org = "org", string ds = "ds", string tool = "build",
        string? requestHash = "rh") =>
        new(org, ds, "hash", "user", "pq", "m.obj", tool, "2", requestHash, "parent", "wf");

    private sealed class Sqlite : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public DbContextOptions<RegistryContext> Options { get; }
        public RegistryContext Db { get; }
        public JobIndexWriter Writer { get; }

        public Sqlite(params IInterceptor[] interceptors)
        {
            Connection.Open();
            var b = new DbContextOptionsBuilder<RegistryContext>().UseSqlite(Connection);
            if (interceptors.Length > 0) b.AddInterceptors(interceptors);
            Options = b.Options;
            Db = new RegistryContext(Options);
            Db.Database.EnsureCreated();
            Writer = PublicationEnv.NewWriter(Db);
        }

        public JobIndex? Row(string id)
        {
            using var db = new RegistryContext(Options);
            return db.JobIndices.AsNoTracking().FirstOrDefault(x => x.JobId == id);
        }

        public void Seed(JobIndex row)
        {
            using var db = new RegistryContext(Options);
            db.JobIndices.Add(row);
            db.SaveChanges();
        }

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
    }

    private static JobIndex Telemetry(string id, string state, DateTime created, IndexPayload? p = null)
    {
        p ??= Payload();
        return new JobIndex
        {
            JobId = id, OrgSlug = p.OrgSlug, DsSlug = p.DsSlug, Hash = "old-hash", Path = "old.obj", UserId = "old-user",
            Queue = "oldq", CreatedAtUtc = created, LastStateChangeUtc = created.AddMinutes(1), CurrentState = state,
            MethodDisplay = "old-method", ToolId = p.ToolId, ToolVersion = "9", RequestHash = p.RequestHash,
            ParentJobId = "old-parent", WorkflowExecutionId = "old-wf", ProcessingAtUtc = created.AddSeconds(1),
            SucceededAtUtc = state == "Succeeded" ? created.AddSeconds(2) : null,
            FailedAtUtc = state == "Failed" ? created.AddSeconds(2) : null,
            DeletedAtUtc = state == "Deleted" ? created.AddSeconds(2) : null,
            ScheduledAtUtc = created.AddSeconds(3), ProgressPercent = 77, PhaseMessage = "phase",
            ArtifactSizeBytes = 99, ArtifactSha256 = "sha", ErrorType = state == "Failed" ? "Boom" : null,
            LogTailJson = "[\"x\"]", ProgressUpdatedAtUtc = created.AddSeconds(4)
        };
    }

    private static string Snap(JobIndex? row) => System.Text.Json.JsonSerializer.Serialize(row);

    // ---------------------------------------------------------------- Inserted

    [Test]
    public async Task Prepare_MissingRow_InsertsIndexingRowAtOriginalCreationTime()
    {
        using var t = new Sqlite();

        var result = await t.Writer.PrepareForPublicationAsync("1", Payload(), Created, "Method()");

        result.ShouldBe(JobIndexPreparationResult.Inserted);
        var row = t.Row("1")!;
        row.CurrentState.ShouldBe("Indexing");
        row.CreatedAtUtc.ShouldBe(Created);
        row.LastStateChangeUtc.ShouldBe(Created);
        row.OrgSlug.ShouldBe("org");
        row.DsSlug.ShouldBe("ds");
        row.Hash.ShouldBe("hash");
        row.Path.ShouldBe("m.obj");
        row.UserId.ShouldBe("user");
        row.Queue.ShouldBe("pq");
        row.ToolId.ShouldBe("build");
        row.ToolVersion.ShouldBe("2");
        row.RequestHash.ShouldBe("rh");
        row.ParentJobId.ShouldBe("parent");
        row.WorkflowExecutionId.ShouldBe("wf");
        row.MethodDisplay.ShouldBe("Method()");
        row.ProcessingAtUtc.ShouldBeNull();
        row.ProgressPercent.ShouldBeNull();
    }

    [Test]
    public async Task Prepare_BlankTool_UsesIndexDefaults()
    {
        using var t = new Sqlite();
        var p = new IndexPayload("o", "d", null, null, ToolId: "", ToolVersion: " ");

        (await t.Writer.PrepareForPublicationAsync("1", p, Created, null)).ShouldBe(JobIndexPreparationResult.Inserted);
        t.Row("1")!.ToolId.ShouldBe("build");
        t.Row("1")!.ToolVersion.ShouldBe("1");
        // Same blank payload again is the same identity.
        (await t.Writer.PrepareForPublicationAsync("1", p, Created, null)).ShouldBe(JobIndexPreparationResult.Compatible);
    }

    [Test]
    public async Task Prepare_InvalidPayload_Throws()
    {
        using var t = new Sqlite();
        await Should.ThrowAsync<ArgumentException>(() =>
            t.Writer.PrepareForPublicationAsync("1", new IndexPayload("", "d", null, null), Created, null));
        t.Row("1").ShouldBeNull();
    }

    // ---------------------------------------------------------------- Compatible: never modified

    [TestCase("Indexing")]
    [TestCase("Created")]
    [TestCase("Enqueued")]
    [TestCase("Scheduled")]
    [TestCase("Processing")]
    [TestCase("Succeeded")]
    [TestCase("Failed")]
    [TestCase("Deleted")]
    public async Task Prepare_CompatibleRow_IsNeverWritten_InAnyState(string state)
    {
        using var t = new Sqlite();
        t.Seed(Telemetry("1", state, Created));
        var before = Snap(t.Row("1"));

        // Different non-identity inputs (hash/path/queue/user/method) must not leak into the row either.
        var result = await t.Writer.PrepareForPublicationAsync("1", Payload() with { Hash = "new", Path = "new.obj" },
            Created, "other-method");

        result.ShouldBe(JobIndexPreparationResult.Compatible);
        Snap(t.Row("1")).ShouldBe(before);
        t.Db.ChangeTracker.Entries().ShouldBeEmpty("the writer context must not keep tracked copies");
    }

    [Test]
    public async Task Prepare_SubMillisecondDifference_IsStillTheSamePublication()
    {
        using var t = new Sqlite();
        await t.Writer.PrepareForPublicationAsync("1", Payload(), Created, null);

        (await t.Writer.PrepareForPublicationAsync("1", Payload(), Created.AddTicks(9_999), null))
            .ShouldBe(JobIndexPreparationResult.Compatible);
        (await t.Writer.PrepareForPublicationAsync("1", Payload(),
            DateTime.SpecifyKind(Created, DateTimeKind.Unspecified), null)).ShouldBe(JobIndexPreparationResult.Compatible);
    }

    // ---------------------------------------------------------------- Incompatible: never modified

    [TestCase("org")]
    [TestCase("ds")]
    [TestCase("tool")]
    [TestCase("request")]
    public async Task Prepare_SameCreationTimeDifferentIdentity_IsIncompatible_AndRowUntouched(string field)
    {
        using var t = new Sqlite();
        var stored = Payload();
        t.Seed(Telemetry("1", "Processing", Created, stored));
        var before = Snap(t.Row("1"));
        var other = field switch
        {
            "org" => Payload(org: "x"),
            "ds" => Payload(ds: "x"),
            "tool" => Payload(tool: "x"),
            _ => Payload(requestHash: "x")
        };

        var result = await t.Writer.PrepareForPublicationAsync("1", other, Created, "m");

        result.ShouldBe(JobIndexPreparationResult.Incompatible);
        Snap(t.Row("1")).ShouldBe(before);
    }

    [Test]
    public async Task Prepare_NullRequestHashVersusStoredHash_IsIncompatible()
    {
        using var t = new Sqlite();
        t.Seed(Telemetry("1", "Enqueued", Created));
        (await t.Writer.PrepareForPublicationAsync("1", Payload(requestHash: null), Created, null))
            .ShouldBe(JobIndexPreparationResult.Incompatible);
    }

    // ---------------------------------------------------------------- Replaced (job id reuse)

    [TestCase("Succeeded")]
    [TestCase("Processing")]
    [TestCase("Failed")]
    [TestCase("Deleted")]
    public async Task Prepare_DifferentCreationTime_ReplacesRowWithFreshIndexingRow(string oldState)
    {
        using var t = new Sqlite();
        t.Seed(Telemetry("1", oldState, Created.AddDays(-10), Payload(org: "old-org", ds: "old-ds")));

        var result = await t.Writer.PrepareForPublicationAsync("1", Payload(), Created, "Method()");

        result.ShouldBe(JobIndexPreparationResult.Replaced);
        var row = t.Row("1")!;
        row.CurrentState.ShouldBe("Indexing");
        row.OrgSlug.ShouldBe("org");
        row.DsSlug.ShouldBe("ds");
        row.Hash.ShouldBe("hash");
        row.Path.ShouldBe("m.obj");
        row.UserId.ShouldBe("user");
        row.Queue.ShouldBe("pq");
        row.ToolVersion.ShouldBe("2");
        row.MethodDisplay.ShouldBe("Method()");
        row.ParentJobId.ShouldBe("parent");
        row.WorkflowExecutionId.ShouldBe("wf");
        row.CreatedAtUtc.ShouldBe(Created);
        row.LastStateChangeUtc.ShouldBe(Created);
        row.ProcessingAtUtc.ShouldBeNull();
        row.SucceededAtUtc.ShouldBeNull();
        row.FailedAtUtc.ShouldBeNull();
        row.DeletedAtUtc.ShouldBeNull();
        row.ScheduledAtUtc.ShouldBeNull();
        row.ProgressPercent.ShouldBeNull();
        row.PhaseMessage.ShouldBeNull();
        row.ArtifactSizeBytes.ShouldBeNull();
        row.ArtifactSha256.ShouldBeNull();
        row.ErrorType.ShouldBeNull();
        row.LogTailJson.ShouldBeNull();
        row.ProgressUpdatedAtUtc.ShouldBeNull();
        t.Db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Test]
    public async Task Prepare_AfterReplace_SecondCallConvergesAndDoesNotResetAdvancedRow()
    {
        using var t = new Sqlite();
        t.Seed(Telemetry("1", "Succeeded", Created.AddDays(-1)));
        (await t.Writer.PrepareForPublicationAsync("1", Payload(), Created, null)).ShouldBe(JobIndexPreparationResult.Replaced);

        // The new job runs and the worker writes telemetry.
        await t.Writer.UpdateStateAsync("1", "Processing", Created.AddMinutes(1));
        await t.Writer.UpdateProgressAsync("1", 50, "half", null, Created.AddMinutes(2));
        var before = Snap(t.Row("1"));

        // A competing preparer of the same publication arrives late.
        using var other = new RegistryContext(t.Options);
        var result = await PublicationEnv.NewWriter(other).PrepareForPublicationAsync("1", Payload(), Created, null);

        result.ShouldBe(JobIndexPreparationResult.Compatible);
        Snap(t.Row("1")).ShouldBe(before);
    }

    [Test]
    public async Task Prepare_ConcurrentPreparers_ConvergeOnOneRow()
    {
        var path = Path.Combine(Path.GetTempPath(), "prepare-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var options = new DbContextOptionsBuilder<RegistryContext>().UseSqlite("Data Source=" + path).Options;
            using (var init = new RegistryContext(options)) init.Database.EnsureCreated();

            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                using var db = new RegistryContext(options);
                return await PublicationEnv.NewWriter(db).PrepareForPublicationAsync("1", Payload(), Created, "m");
            })));

            results.Count(r => r == JobIndexPreparationResult.Inserted).ShouldBe(1);
            results.Count(r => r == JobIndexPreparationResult.Compatible).ShouldBe(7);
            using var check = new RegistryContext(options);
            check.JobIndices.Count().ShouldBe(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- failure handling in a reused context

    private sealed class FailingSaveInterceptor(Func<int, Exception?> failFor) : SaveChangesInterceptor
    {
        private int _calls;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var failure = failFor(++_calls);
            if (failure != null) throw failure;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Test]
    public async Task Prepare_FailedSave_DoesNotPoisonLaterSavesInTheSameContext()
    {
        using var t = new Sqlite(new FailingSaveInterceptor(n =>
            n == 1 ? new InvalidOperationException("disk full") : null));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            t.Writer.PrepareForPublicationAsync("1", Payload(), Created, null));
        t.Db.ChangeTracker.Entries().ShouldBeEmpty("the failed insert must be detached");

        // A later, unrelated save through the same context must not replay the failed insert.
        (await t.Writer.PrepareForPublicationAsync("2", Payload(), Created, null)).ShouldBe(JobIndexPreparationResult.Inserted);

        t.Row("1").ShouldBeNull();
        t.Row("2").ShouldNotBeNull();
    }

    [Test]
    public async Task Prepare_TransientDbUpdateException_IsReclassifiedOnRetry()
    {
        using var t = new Sqlite(new FailingSaveInterceptor(n =>
            n == 1 ? new DbUpdateException("duplicate key") : null));

        // The writer treats the failure as a lost insert race and re-reads; the row is absent so it inserts.
        (await t.Writer.PrepareForPublicationAsync("1", Payload(), Created, null)).ShouldBe(JobIndexPreparationResult.Inserted);
        t.Row("1")!.CurrentState.ShouldBe("Indexing");
        t.Db.ChangeTracker.Entries<JobIndex>().Count(e => e.State != EntityState.Unchanged).ShouldBe(0);
    }

    [Test]
    public async Task Prepare_PersistentDbUpdateException_IsBoundedAndPropagates()
    {
        using var t = new Sqlite(new FailingSaveInterceptor(_ => new DbUpdateException("always")));
        await Should.ThrowAsync<DbUpdateException>(() => t.Writer.PrepareForPublicationAsync("1", Payload(), Created, null));
        t.Db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- non-relational provider

    private static (RegistryContext Db, JobIndexWriter Writer) InMemory()
    {
        var db = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("prep-" + Guid.NewGuid()).Options);
        return (db, PublicationEnv.NewWriter(db));
    }

    [Test]
    public async Task InMemoryProvider_InsertCompatibleIncompatibleReplace()
    {
        var (db, writer) = InMemory();
        using var _ = db;

        (await writer.PrepareForPublicationAsync("1", Payload(), Created, null)).ShouldBe(JobIndexPreparationResult.Inserted);
        db.JobIndices.AsNoTracking().Single().CurrentState.ShouldBe("Indexing");

        await writer.UpdateStateAsync("1", "Processing", Created.AddSeconds(5));
        var processing = Snap(db.JobIndices.AsNoTracking().Single());
        (await writer.PrepareForPublicationAsync("1", Payload(), Created, null)).ShouldBe(JobIndexPreparationResult.Compatible);
        (await writer.PrepareForPublicationAsync("1", Payload(org: "x"), Created, null)).ShouldBe(JobIndexPreparationResult.Incompatible);
        Snap(db.JobIndices.AsNoTracking().Single()).ShouldBe(processing);

        (await writer.PrepareForPublicationAsync("1", Payload(org: "x"), Created.AddDays(1), null))
            .ShouldBe(JobIndexPreparationResult.Replaced);
        var row = db.JobIndices.AsNoTracking().Single();
        row.CurrentState.ShouldBe("Indexing");
        row.OrgSlug.ShouldBe("x");
        row.ProcessingAtUtc.ShouldBeNull();
    }

    // ---------------------------------------------------------------- UpdateStateIfCurrentAsync

    private static readonly DateTime Changed = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestCase("Processing")]
    [TestCase("Succeeded")]
    [TestCase("Failed")]
    [TestCase("Deleted")]
    [TestCase("Scheduled")]
    [TestCase("Enqueued")]
    public async Task UpdateIfCurrent_Relational_UpdatesWhenExpected_WithSameStampingAsUpdateState(string newState)
    {
        using var t = new Sqlite();
        t.Seed(Telemetry("a", "Indexing", Created));
        t.Seed(Telemetry("b", "Indexing", Created));
        t.Db.JobIndices.AsTracking().ToList(); // a stale tracked copy must be dropped, not resurrected

        (await t.Writer.UpdateStateIfCurrentAsync("a", ["Created", "Indexing"], newState, Changed)).ShouldBeTrue();
        await t.Writer.UpdateStateAsync("b", newState, Changed);

        var a = t.Row("a")!;
        var b = t.Row("b")!;
        a.CurrentState.ShouldBe(newState);
        b.JobId = a.JobId;
        Snap(a).ShouldBe(Snap(b));
        a.LastStateChangeUtc.ShouldBe(Changed);
        t.Db.ChangeTracker.Entries<JobIndex>().ShouldNotContain(e => e.Entity.JobId == "a",
            "the set-based update must drop the stale tracked copy");
    }

    [Test]
    public async Task UpdateIfCurrent_Processing_StampsAndClearsError()
    {
        using var t = new Sqlite();
        var row = Telemetry("a", "Indexing", Created);
        row.ErrorType = "Old";
        t.Seed(row);

        (await t.Writer.UpdateStateIfCurrentAsync("a", ["Indexing"], "Processing", Changed)).ShouldBeTrue();

        var after = t.Row("a")!;
        after.ProcessingAtUtc.ShouldBe(Changed);
        after.ErrorType.ShouldBeNull();
    }

    [TestCase("Processing")]
    [TestCase("Succeeded")]
    [TestCase("Failed")]
    [TestCase("Deleted")]
    [TestCase("Enqueued")]
    public async Task UpdateIfCurrent_RowAlreadyAdvanced_ReturnsFalse_AndKeepsEverything(string actual)
    {
        using var t = new Sqlite();
        t.Seed(Telemetry("a", actual, Created));
        var before = Snap(t.Row("a"));

        (await t.Writer.UpdateStateIfCurrentAsync("a", ["Created", "Indexing"], "Deleted", Changed)).ShouldBeFalse();

        Snap(t.Row("a")).ShouldBe(before);
    }

    [Test]
    public async Task UpdateIfCurrent_MissingRow_ReturnsFalse_AndCreatesNothing()
    {
        using var t = new Sqlite();
        (await t.Writer.UpdateStateIfCurrentAsync("nope", ["Indexing"], "Deleted", Changed)).ShouldBeFalse();
        t.Db.JobIndices.Count().ShouldBe(0);
    }

    [Test]
    public async Task UpdateIfCurrent_RaceBetweenReadAndWrite_DoesNotOverwriteAdvancedRow()
    {
        // The guard is evaluated by the database: a row advanced after the writer's read is not overwritten.
        using var t = new Sqlite();
        t.Seed(Telemetry("a", "Indexing", Created));
        using var racing = new RegistryContext(t.Options);
        var row = racing.JobIndices.AsTracking().Single();
        row.CurrentState = "Processing";
        racing.SaveChanges();

        (await t.Writer.UpdateStateIfCurrentAsync("a", ["Indexing"], "Deleted", Changed)).ShouldBeFalse();
        t.Row("a")!.CurrentState.ShouldBe("Processing");
        t.Row("a")!.DeletedAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task UpdateIfCurrent_InMemoryProvider_TrueAndFalsePaths()
    {
        var (db, writer) = InMemory();
        using var _ = db;
        db.JobIndices.Add(Telemetry("a", "Indexing", Created));
        db.JobIndices.Add(Telemetry("b", "Processing", Created));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await writer.UpdateStateIfCurrentAsync("a", ["Indexing"], "Deleted", Changed)).ShouldBeTrue();
        (await writer.UpdateStateIfCurrentAsync("b", ["Indexing"], "Deleted", Changed)).ShouldBeFalse();
        (await writer.UpdateStateIfCurrentAsync("zzz", ["Indexing"], "Deleted", Changed)).ShouldBeFalse();

        var a = db.JobIndices.AsNoTracking().Single(x => x.JobId == "a");
        a.CurrentState.ShouldBe("Deleted");
        a.DeletedAtUtc.ShouldBe(Changed);
        db.JobIndices.AsNoTracking().Single(x => x.JobId == "b").CurrentState.ShouldBe("Processing");
    }

    [Test]
    public async Task UpdateIfCurrent_FailedSave_DoesNotPoisonContext_InMemoryProviderPath()
    {
        var db = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase("prep-" + Guid.NewGuid())
            .AddInterceptors(new FailingSaveInterceptor(n => n == 2 ? new InvalidOperationException("boom") : null))
            .Options);
        using var _ = db;
        var writer = PublicationEnv.NewWriter(db);
        db.JobIndices.Add(Telemetry("a", "Indexing", Created));
        await db.SaveChangesAsync();                                   // save #1
        db.ChangeTracker.Clear();

        await Should.ThrowAsync<InvalidOperationException>(() =>       // save #2 fails
            writer.UpdateStateIfCurrentAsync("a", ["Indexing"], "Deleted", Changed));
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await writer.UpdateStateIfCurrentAsync("a", ["Indexing"], "Deleted", Changed)).ShouldBeTrue();
    }
}
