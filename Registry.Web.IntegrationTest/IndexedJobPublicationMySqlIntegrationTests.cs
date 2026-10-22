#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.MySql;
using Hangfire.Server;
using Moq;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;
using Registry.Web.Services.Caching;

namespace Registry.Web.Test;

/// <summary>
/// Spike gate for the indexed-job publication/recovery design against the shipped MySQL Hangfire provider:
/// atomic state + recovery-set member, exact float score round-trip, expected-state transitions and
/// bounded lowest-score traversal, on the shared MariaDB test server (<see cref="MariaDbServer"/>,
/// Docker); each test owns only its random table prefix.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Network)]
public class IndexedJobPublicationMySqlIntegrationTests
{
    private string _connection = null!;
    private string _prefix = null!;
    private string _root = null!;
    private MySqlStorage _storage = null!;
    private BackgroundJobClient _client = null!;
    private JobStorage? _previousStorage;
    private JobFilter[] _previousFilters = [];
    private IStateHandler[] _previousHandlers = [];
    private bool _initialized;

    public static void Noop() { }

    [SetUp]
    public async Task SetUp()
    {
        _connection = (await MariaDbServer.GetAsync()).ConnectionString;

        _root = Path.Combine(Path.GetTempPath(), "registry-pub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _prefix = "pub" + Guid.NewGuid().ToString("N")[..12];
        _previousFilters = GlobalJobFilters.Filters.ToArray();
        _previousHandlers = GlobalStateHandlers.Handlers.ToArray();
        try { _previousStorage = JobStorage.Current; }
        catch (InvalidOperationException) { _previousStorage = null; }
        _initialized = true;

        _storage = new MySqlStorage(_connection, new MySqlStorageOptions
        {
            TransactionIsolationLevel = System.Transactions.IsolationLevel.ReadCommitted,
            QueuePollInterval = TimeSpan.FromMilliseconds(100),
            PrepareSchemaIfNecessary = true,
            TablesPrefix = _prefix
        });
        JobStorage.Current = _storage;
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
        IndexingStateHandler.EnsureRegistered();
        _client = new BackgroundJobClient(_storage);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (!_initialized) return;
        GlobalJobFilters.Filters.Clear();
        foreach (var f in _previousFilters) GlobalJobFilters.Filters.Add(f.Instance, f.Order);
        GlobalStateHandlers.Handlers.Clear();
        foreach (var h in _previousHandlers) GlobalStateHandlers.Handlers.Add(h);
        if (_previousStorage != null) JobStorage.Current = _previousStorage;

        // Drop only the exact random prefix owned by this test.
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        foreach (var table in await TablesOfPrefix(connection))
        {
            await using var c = connection.CreateCommand();
            c.CommandText = "SET FOREIGN_KEY_CHECKS=0; DROP TABLE `" + table.Replace("`", "``") + "`; SET FOREIGN_KEY_CHECKS=1;";
            await c.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        _initialized = false;
    }

    private async Task<List<string>> TablesOfPrefix(MySqlConnection connection)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var t = reader.GetString(0);
            if (t.StartsWith(_prefix, StringComparison.Ordinal)) tables.Add(t);
        }
        return tables;
    }

    private static IndexedJobPublicationRecord Record(DateTime? created = null, string ds = "ds") =>
        IndexedJobPublicationRecord.ForImmediate(new IndexPayload("org", ds, null, "user"),
            created ?? DateTime.UtcNow, "default");

    private string CreateIndexing(IndexedJobPublicationRecord record) =>
        _client.Create(Job.FromExpression(() => Noop()), new IndexingState(record));

    private async Task<string> SetTable()
    {
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        return (await TablesOfPrefix(connection)).Single(t => t.EndsWith("Set", StringComparison.Ordinal));
    }

    private async Task<List<(string Value, double Score)>> SetRows(string key)
    {
        var table = await SetTable();
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        await using var c = connection.CreateCommand();
        c.CommandText = $"SELECT `Value`, `Score` FROM `{table}` WHERE `Key`=@k";
        c.Parameters.AddWithValue("@k", key);
        var rows = new List<(string, double)>();
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetDouble(1)));
        return rows;
    }

    private string? StateName(string id)
    {
        using var c = _storage.GetConnection();
        return c.GetStateData(id)?.Name;
    }

    private static string Key => IndexingStateHandler.RecoverySetKey;

    [Test]
    public async Task Create_PersistsIndexingStateAndRecoveryMemberWithExactScore()
    {
        var created = new DateTime(2026, 10, 13, 12, 34, 56, 789, DateTimeKind.Utc);
        var record = Record(created);
        var id = CreateIndexing(record);

        using (var c = _storage.GetConnection())
        {
            var state = c.GetStateData(id);
            state.Name.ShouldBe("Indexing");
            var parsed = IndexedJobPublicationRecord.TryParse(state.Data);
            parsed.Status.ShouldBe(IndexedJobRecordParseStatus.Valid);
            parsed.Record!.CreatedAtUtc.ShouldBe(record.CreatedAtUtc);
            parsed.Record.Payload.ShouldBe(record.Payload);
        }

        var expected = IndexingStateHandler.ToScore(created + IndexingStateHandler.GracePeriod);
        var rows = await SetRows(Key);
        rows.Count.ShouldBe(1);
        rows[0].Value.ShouldBe(id);
        rows[0].Score.ShouldBe(expected); // exact, no tolerance
        IndexingStateHandler.FromScore(rows[0].Score).ShouldBe(
            IndexingStateHandler.FromScore(expected));

        // Provider read path agrees with the bounds.
        using var conn = _storage.GetConnection();
        conn.GetFirstByLowestScoreFromSet(Key, expected, expected).ShouldBe(id);
        conn.GetFirstByLowestScoreFromSet(Key, expected + 1, expected + 100).ShouldBeNull();
        conn.GetFirstByLowestScoreFromSet(Key, 0, expected - 1).ShouldBeNull();
    }

    [Test]
    public async Task Scores_RoundTripExactlyAcrossRepresentativeMinutes()
    {
        // 0, small, current-era and the largest exactly-representable single-precision whole number.
        // Reading the FLOAT column back as text keeps only 6 significant digits (observed on MariaDB:
        // 9,999,999 reads as 10,000,000), so a score read as a value is exact only below 1,000,000 minutes
        // (2027-11-26 with the 2026-01-01 epoch). Stored values stay exact up to 2^24 and SQL range
        // comparisons use them, which the bounds assertions below prove.
        var scores = new double[] { 0, 1, 1439, 525_600, 999_999 };
        using (var c = _storage.GetConnection())
            foreach (var s in scores) c.CreateWriteTransaction().Also(t => { t.AddToSet("registry:rt", "m" + s, s); t.Commit(); });
        var rows = (await SetRows("registry:rt")).ToDictionary(r => r.Value, r => r.Score);
        foreach (var s in scores) rows["m" + s].ShouldBe(s);

        // Stored value stays exact up to 2^24 even where the displayed value is rounded; SQL range
        // comparisons (what GetFirstByLowestScoreFromSet uses) operate on the stored value.
        using var conn = _storage.GetConnection();
        foreach (var big in new double[] { 1_000_001, 9_999_999, 16_777_215 })
        {
            using (var c = _storage.GetConnection())
            {
                var t = c.CreateWriteTransaction();
                t.AddToSet("registry:rt-big", "big" + big, big);
                t.Commit();
            }
            conn.GetFirstByLowestScoreFromSet("registry:rt-big", big, big).ShouldBe("big" + big);
            conn.GetFirstByLowestScoreFromSet("registry:rt-big", big - 1, big - 1).ShouldBeNull();
        }
    }

    private sealed class FailOnIndexingApplied : JobFilterAttribute, IApplyStateFilter
    {
        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (context.NewState.Name == "Indexing") throw new InvalidOperationException("injected after handler");
        }
        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction) { }
    }

    [Test]
    public async Task FaultInjectedInStateApplied_CreateThrowsAndLeavesNoMemberOrIndexingState()
    {
        var filter = new FailOnIndexingApplied();
        GlobalJobFilters.Filters.Add(filter);
        try
        {
            Should.Throw<Exception>(() => CreateIndexing(Record()));
        }
        finally { GlobalJobFilters.Filters.Remove(filter); }

        (await SetRows(Key)).ShouldBeEmpty();
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        var jobTable = (await TablesOfPrefix(connection)).Single(t => t.EndsWith("Job", StringComparison.Ordinal));
        await using var c = connection.CreateCommand();
        c.CommandText = $"SELECT COUNT(*) FROM `{jobTable}` WHERE `StateName`='Indexing'";
        Convert.ToInt64(await c.ExecuteScalarAsync()).ShouldBe(0);

        // The storage is healthy afterwards.
        var id = CreateIndexing(Record());
        StateName(id).ShouldBe("Indexing");
        (await SetRows(Key)).Count.ShouldBe(1);
    }

    [Test]
    public async Task ChangeStateFromIndexing_RemovesMemberInSameTransition_AndOnlyOneTransitionWins()
    {
        var id = CreateIndexing(Record());
        (await SetRows(Key)).Count.ShouldBe(1);

        _client.ChangeState(id, new EnqueuedState(), "Indexing").ShouldBeTrue();
        StateName(id).ShouldBe("Enqueued");
        (await SetRows(Key)).ShouldBeEmpty();

        _client.ChangeState(id, new EnqueuedState(), "Indexing").ShouldBeFalse();
        _client.ChangeState(id, new ScheduledState(DateTime.UtcNow.AddHours(1)), "Indexing").ShouldBeFalse();
        StateName(id).ShouldBe("Enqueued");
        (await SetRows(Key)).ShouldBeEmpty();
    }

    /// <summary>
    /// Hangfire.MySqlStorage 2.0.3 takes its distributed lock with a racy INSERT ... WHERE NOT EXISTS, so
    /// concurrent ChangeState calls may all pass the expected-state check. The design therefore does not rely
    /// on a single winner: every racing publisher writes the same destination and the outcome converges.
    /// </summary>
    [Test]
    public async Task ConcurrentChangeStateFromIndexing_Converges()
    {
        for (var round = 0; round < 3; round++)
        {
            var id = CreateIndexing(Record());
            using (var c = _storage.GetConnection())
            {
                c.SetJobParameter(id, JobParamKeys.OrgSlug, "org");
                c.SetJobParameter(id, JobParamKeys.DsSlug, "ds");
            }

            const int n = 12;
            using var gate = new ManualResetEventSlim();
            var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(() =>
            {
                var client = new BackgroundJobClient(_storage);
                gate.Wait();
                return client.ChangeState(id, new EnqueuedState(), "Indexing");
            })).ToArray();
            gate.Set();
            var results = await Task.WhenAll(tasks);

            // Winners are reported only: the provider lock is not mutually exclusive (see the
            // DistributedLock characterization below), so more than one caller may report success.
            TestContext.Out.WriteLine($"round {round}: {results.Count(x => x)} of {n} ChangeState calls reported success");
            results.Count(x => x).ShouldBeGreaterThanOrEqualTo(1);

            StateName(id).ShouldBe("Enqueued");
            (await SetRows(Key)).Any(r => r.Value == id).ShouldBeFalse("no recovery-set member after leaving Indexing");
            using var conn = _storage.GetConnection();
            conn.GetJobParameter(id, JobParamKeys.OrgSlug).ShouldBe("org");
            conn.GetJobParameter(id, JobParamKeys.DsSlug).ShouldBe("ds");
            // Further losers are rejected cleanly once the job left Indexing.
            _client.ChangeState(id, new EnqueuedState(), "Indexing").ShouldBeFalse();
        }
    }

    /// <summary>
    /// Provider characterization, not a requirement of the design: documents that the MySqlStorage 2.0.3
    /// distributed lock is not reliably mutually exclusive across connections.
    /// </summary>
    [Test]
    [Explicit("Characterizes a known Hangfire.MySqlStorage 2.0.3 limitation: the distributed lock (racy INSERT ... WHERE NOT EXISTS) " +
              "is not mutually exclusive across connections. The publication design does not depend on it; this test is expected to fail on the shipped provider.")]
    public void DistributedLock_IsMutuallyExclusiveAcrossConnections()
    {
        const int n = 12;
        var inside = 0;
        var maxInside = 0;
        using var gate = new ManualResetEventSlim();
        using var requested = new CountdownEvent(n);
        var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(() =>
        {
            using var c = _storage.GetConnection();
            gate.Wait();
            requested.Signal();
            using (c.AcquireDistributedLock("job:spike:state-lock", TimeSpan.FromSeconds(30)))
            {
                var now = Interlocked.Increment(ref inside);
                int seen;
                while ((seen = Volatile.Read(ref maxInside)) < now &&
                       Interlocked.CompareExchange(ref maxInside, now, seen) != seen) { }
                // Hold the lock until every caller is asking for it: an exclusive lock keeps them
                // all out meanwhile (they signal before acquiring, so this cannot deadlock)
                requested.Wait();
                Interlocked.Decrement(ref inside);
            }
        })).ToArray();
        gate.Set();
        Task.WaitAll(tasks);
        TestContext.Out.WriteLine("max concurrent lock holders observed: " + maxInside);
        maxInside.ShouldBe(1);
    }

    [Test]
    public async Task Traversal_LowestDueFirst_UpsertReschedules_AndFutureMembersAreNotReturned()
    {
        var now = new DateTime(2026, 10, 13, 12, 0, 30, DateTimeKind.Utc);
        var bound = IndexingStateHandler.ScoreForNow(now);
        double Due(int minutesAgo) => IndexingStateHandler.ToScore(now.AddMinutes(-minutesAgo));
        using (var c = _storage.GetConnection())
        {
            var t = c.CreateWriteTransaction();
            t.AddToSet(Key, "c", Due(10));
            t.AddToSet(Key, "a", Due(30));
            t.AddToSet(Key, "b", Due(20));
            t.AddToSet(Key, "future", IndexingStateHandler.ToScore(now.AddMinutes(5)));
            t.Commit();
        }

        using var conn = _storage.GetConnection();
        conn.GetFirstByLowestScoreFromSet(Key, 0, bound).ShouldBe("a");

        // Reschedule a into the future: upsert, not a new row.
        var future = IndexingStateHandler.ToScore(now.AddMinutes(1));
        using (var c = _storage.GetConnection())
        {
            var t = c.CreateWriteTransaction();
            t.AddToSet(Key, "a", future);
            t.Commit();
        }
        (await SetRows(Key)).Count.ShouldBe(4);
        (await SetRows(Key)).Single(r => r.Value == "a").Score.ShouldBe(future);
        conn.GetFirstByLowestScoreFromSet(Key, 0, bound).ShouldBe("b");

        foreach (var expected in new[] { "b", "c" })
        {
            conn.GetFirstByLowestScoreFromSet(Key, 0, bound).ShouldBe(expected);
            using var c = _storage.GetConnection();
            var t = c.CreateWriteTransaction();
            t.AddToSet(Key, expected, IndexingStateHandler.ToScore(now.AddMinutes(1)));
            t.Commit();
        }
        // Everything rescheduled with ToScore(now + 1 min), or in the future, is outside the bound.
        conn.GetFirstByLowestScoreFromSet(Key, 0, bound).ShouldBeNull();
        (await SetRows(Key)).Count.ShouldBe(4);
        (await SetRows(Key)).Single(r => r.Value == "b").Score.ShouldBeGreaterThan(bound);
    }

    [Test]
    public async Task Schema_ComesFromProviderAutoInstallOnly()
    {
        CreateIndexing(Record());
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        var tables = (await TablesOfPrefix(connection)).Select(t => t[_prefix.Length..].TrimStart('_')).OrderBy(x => x).ToList();
        TestContext.Progress.WriteLine("Tables created by MySqlStorage auto-install: " + string.Join(", ", tables));
        tables.ShouldContain("Set");
        tables.ShouldContain("Job");
        tables.ShouldContain("State");
        // Only provider tables exist under the prefix; nothing app-specific was needed.
        tables.ShouldNotContain(t => t.Contains("Recovery", StringComparison.OrdinalIgnoreCase));
    }

    private ServiceProvider BuildApp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache().AddRecordCache();
        services.AddDbContext<RegistryContext>(o => o.UseSqlite("Data Source=" + Path.Combine(_root, "registry.sqlite")));
        services.AddScoped<IJobIndexWriter, JobIndexWriter>();
        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<RegistryContext>().Database.EnsureCreated();
        return sp;
    }

    [Test]
    public async Task EndToEnd_Enqueuer_PublishesJobAndRemovesMember()
    {
        using var sp = BuildApp();
        var enqueuer = new IndexedJobEnqueuer(_client, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IndexedJobEnqueuer>.Instance, _storage);

        var immediate = enqueuer.Enqueue(() => Noop(), new IndexPayload("org", "ds", null, "user"));
        var scheduled = enqueuer.Schedule(() => Noop(), new IndexPayload("org", "ds", null, "user"), TimeSpan.FromHours(1));

        StateName(immediate).ShouldBe("Enqueued");
        StateName(scheduled).ShouldBe("Scheduled");
        (await SetRows(Key)).ShouldBeEmpty();
        using var c = _storage.GetConnection();
        c.GetJobParameter(immediate, JobParamKeys.OrgSlug).ShouldBe("org");
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        (await db.JobIndices.CountAsync(x => x.JobId == immediate || x.JobId == scheduled)).ShouldBe(2);
    }

    [Test]
    public async Task EndToEnd_PublishFailureLeavesIndexingJobWithMember()
    {
        // JobIndex writer that always fails: creator logs, returns the id, recovery set keeps the job.
        var services = new ServiceCollection();
        services.AddScoped<IJobIndexWriter>(_ => throw new InvalidOperationException("index down"));
        using var sp = services.BuildServiceProvider();
        var enqueuer = new IndexedJobEnqueuer(_client, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IndexedJobEnqueuer>.Instance, _storage);

        var id = enqueuer.Enqueue(() => Noop(), new IndexPayload("org", "ds", null, "user"));

        StateName(id).ShouldBe("Indexing");
        (await SetRows(Key)).Select(r => r.Value).ShouldBe([id]);
    }

    [Test]
    public async Task PrepareForPublication_ConcurrentReplacedPathConverges()
    {
        using var sp = BuildApp();
        var meta = new IndexPayload("org", "ds", null, "user");
        var first = new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var second = first.AddMinutes(7);
        using (var scope = sp.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IJobIndexWriter>()
                .PrepareForPublicationAsync("reuse-1", meta, first, "m")).ShouldBe(JobIndexPreparationResult.Inserted);

        const int n = 6;
        using var gate = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, n).Select(_ => Task.Run(async () =>
        {
            using var scope = sp.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            gate.Wait();
            return await writer.PrepareForPublicationAsync("reuse-1", meta, second, "m");
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r == JobIndexPreparationResult.Replaced).ShouldBe(1);
        results.Count(r => r == JobIndexPreparationResult.Compatible).ShouldBe(n - 1);
        using var check = sp.CreateScope();
        var row = await check.ServiceProvider.GetRequiredService<RegistryContext>().JobIndices
            .AsNoTracking().SingleAsync(x => x.JobId == "reuse-1");
        row.CreatedAtUtc.ShouldBe(second);
        row.CurrentState.ShouldBe("Indexing");
    }
}

internal static class SpikeExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}


/// <summary>Static job targets for the real-worker publication tests; they record executions by Hangfire job id.</summary>
public static class PubProbe
{
    public static readonly ConcurrentDictionary<string, int> Runs = new();

    private static readonly ConcurrentDictionary<string, TaskCompletionSource> RunSignals = new();

    /// <summary>Per job id: empty when parameters and JobIndex row existed at execution time, otherwise the problem.</summary>
    public static readonly ConcurrentDictionary<string, string> Checks = new();

    /// <summary>Set by the fixture; returns "" when the publication is complete for the job id.</summary>
    public static Func<PerformContext, string>? Inspect;

    public static void Reset()
    {
        Runs.Clear();
        Checks.Clear();
        RunSignals.Clear();
        Inspect = null;
    }

    /// <summary>Completes when a worker has run the job (signalled by the job body itself).</summary>
    public static Task WhenRun(string id) => Signal(id).Task.WaitAsync(TimeSpan.FromSeconds(30));

    private static TaskCompletionSource Signal(string id) =>
        RunSignals.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void Run(string tag, PerformContext? context = null)
    {
        var id = context!.BackgroundJob.Id;
        // A failing inspection is a finding, not a job failure: the test reads it from Checks at once
        // instead of timing out on a job that never signalled
        try { Checks[id] = Inspect?.Invoke(context) ?? ""; }
        catch (Exception e) { Checks[id] = "inspection failed: " + e; }
        Runs.AddOrUpdate(id, 1, (_, n) => n + 1);
        Signal(id).TrySetResult();
    }
}

/// <summary>
/// Real MariaDB + real Hangfire worker regressions for interrupted indexed-job publication
/// (A1, A2, A6, A7, malformed records). No native library is involved.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Network)]
public class IndexedJobPublicationWorkerIntegrationTests
{
    private string _connection = null!;
    private string _prefix = null!;
    private string _root = null!;
    private MySqlStorage _storage = null!;
    private ServiceProvider _provider = null!;
    private BackgroundJobServer? _server;
    private JobStorage? _previousStorage;
    private JobFilter[] _previousFilters = [];
    private IStateHandler[] _previousHandlers = [];
    private bool _initialized;

    private static string Key => IndexingStateHandler.RecoverySetKey;
    private BackgroundJobClient Client => new(_storage);

    private sealed class ThrowingChangeStateClient(IBackgroundJobClient inner) : IBackgroundJobClient
    {
        public string Create(Job job, IState state) => inner.Create(job, state);
        public bool ChangeState(string jobId, IState state, string expectedState) =>
            throw new InvalidOperationException("publisher interrupted before the final transition");
    }

    [SetUp]
    public async Task SetUp()
    {
        _connection = (await MariaDbServer.GetAsync()).ConnectionString;

        PubProbe.Reset();
        lock (_log.Entries) _log.Entries.Clear();
        _root = Path.Combine(Path.GetTempPath(), "registry-pubw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _prefix = "pubw" + Guid.NewGuid().ToString("N")[..12];
        _previousFilters = GlobalJobFilters.Filters.ToArray();
        _previousHandlers = GlobalStateHandlers.Handlers.ToArray();
        try { _previousStorage = JobStorage.Current; }
        catch (InvalidOperationException) { _previousStorage = null; }
        _initialized = true;

        _storage = new MySqlStorage(_connection, new MySqlStorageOptions
        {
            TransactionIsolationLevel = System.Transactions.IsolationLevel.ReadCommitted,
            QueuePollInterval = TimeSpan.FromMilliseconds(100),
            InvisibilityTimeout = TimeSpan.FromMinutes(5),
            PrepareSchemaIfNecessary = true,
            TablesPrefix = _prefix
        });
        JobStorage.Current = _storage;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache().AddRecordCache();
        services.AddDbContext<RegistryContext>(o => o.UseSqlite("Data Source=" + Path.Combine(_root, "registry.sqlite")));
        services.AddScoped<IJobIndexWriter, JobIndexWriter>();
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RegistryContext>().Database.EnsureCreatedAsync();

        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
        GlobalJobFilters.Filters.Clear();
        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 0 });
        IndexingStateHandler.EnsureRegistered();
        GlobalJobFilters.Filters.Add(new JobIndexStateFilter(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<JobIndexStateFilter>>()));

        PubProbe.Inspect = ctx =>
        {
            using var conn = ctx.Storage.GetConnection();
            var id = ctx.BackgroundJob.Id;
            var problems = new List<string>();
            if (conn.GetJobParameter(id, JobParamKeys.OrgSlug) != "org") problems.Add("orgSlug parameter missing");
            if (conn.GetJobParameter(id, JobParamKeys.DsSlug) == null) problems.Add("dsSlug parameter missing");
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            if (!db.JobIndices.AsNoTracking().Any(x => x.JobId == id)) problems.Add("JobIndex row missing");
            return string.Join("; ", problems);
        };
    }

    [TearDown]
    public async Task TearDown()
    {
        if (!_initialized) return;
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            foreach (var e in _log.Entries) TestContext.Out.WriteLine($"[recovery {e}]");
            // A job that failed before its body ran never signals PubProbe: show why
            foreach (var failed in _storage.GetMonitoringApi().FailedJobs(0, 10))
                TestContext.Out.WriteLine($"[failed job {failed.Key}] {failed.Value.ExceptionType}: {failed.Value.ExceptionMessage}");
        }
        _server?.Dispose();
        _server = null;
        _provider?.Dispose();
        GlobalJobFilters.Filters.Clear();
        foreach (var f in _previousFilters) GlobalJobFilters.Filters.Add(f.Instance, f.Order);
        GlobalStateHandlers.Handlers.Clear();
        foreach (var h in _previousHandlers) GlobalStateHandlers.Handlers.Add(h);
        if (_previousStorage != null) JobStorage.Current = _previousStorage;
        PubProbe.Reset();

        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        foreach (var table in await TablesOfPrefix(connection))
        {
            await using var c = connection.CreateCommand();
            c.CommandText = "SET FOREIGN_KEY_CHECKS=0; DROP TABLE `" + table.Replace("`", "``") + "`; SET FOREIGN_KEY_CHECKS=1;";
            await c.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        _initialized = false;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<List<string>> TablesOfPrefix(MySqlConnection connection)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var t = reader.GetString(0);
            if (t.StartsWith(_prefix, StringComparison.Ordinal)) tables.Add(t);
        }
        return tables;
    }

    private async Task<long> Scalar(string tableSuffix, string where = "1=1")
    {
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        var table = (await TablesOfPrefix(connection)).Single(t => t.EndsWith(tableSuffix, StringComparison.Ordinal));
        await using var c = connection.CreateCommand();
        c.CommandText = $"SELECT COUNT(*) FROM `{table}` WHERE {where}";
        return Convert.ToInt64(await c.ExecuteScalarAsync());
    }

    private Task<long> JobCount() => Scalar("Job");

    private async Task<List<(string Value, double Score)>> SetRows()
    {
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        var table = (await TablesOfPrefix(connection)).Single(t => t.EndsWith("Set", StringComparison.Ordinal));
        await using var c = connection.CreateCommand();
        c.CommandText = $"SELECT `Value`, `Score` FROM `{table}` WHERE `Key`=@k";
        c.Parameters.AddWithValue("@k", Key);
        var rows = new List<(string, double)>();
        await using var r = await c.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetDouble(1)));
        return rows;
    }

    private StateData? State(string id)
    {
        using var c = _storage.GetConnection();
        return c.GetStateData(id);
    }

    private async Task<JobIndex?> Row(string id)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RegistryContext>().JobIndices.AsNoTracking()
            .SingleOrDefaultAsync(x => x.JobId == id);
    }

    // JobIndex rows and queue rows change in worker transactions: no event to await
    private static Task WaitUntil(Func<Task<bool>> condition, string what) =>
        Eventually.Until(condition, what, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Rows of the job in the provider's queue table. A worker only runs a job it fetched from
    /// there, so a job without a row (and in a non-enqueued state) cannot run.
    /// </summary>
    private Task<long> QueuedRows(string id) =>
        Scalar("JobQueue", "`JobId`=" + long.Parse(id, System.Globalization.CultureInfo.InvariantCulture));

    private static IndexPayload Payload(string ds = "ds", string queue = "pq") =>
        new("org", ds, "hash-1", "user-1", queue, "model.obj", "build", "7", "rh-" + ds, "parent-1", "wf-1");

    private static Job ProbeJob(string tag = "t") => Job.FromExpression(() => PubProbe.Run(tag, null!));

    // Own activator: the worker begins an activator scope even for static jobs, and the process-global
    // JobActivator.Current may still be the one a disposed test host (Hangfire.AspNetCore) installed
    private void StartServer() => _server = new BackgroundJobServer(new BackgroundJobServerOptions
    {
        Activator = new JobActivator(),
        WorkerCount = 2,
        Queues = ["pq", "default"],
        SchedulePollingInterval = TimeSpan.FromSeconds(1),
        ShutdownTimeout = TimeSpan.FromSeconds(10),
        ServerName = "pubw-" + _prefix
    }, _storage);

    private IndexedJobPublicationRecoveryService NewService(IJobIndexWriter writer, FakeTime time) =>
        new(writer, _log, _storage, time);

    private readonly ExceptionCapturingLogger<IndexedJobPublicationRecoveryService> _log = new();

    private static FakeTime TimeAt(TimeSpan fromNow) => new(FakeTime.CeilMinute(DateTime.UtcNow + fromNow));

    private IndexedJobEnqueuer EnqueuerWith(IBackgroundJobClient client, IServiceProvider sp) =>
        new(client, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IndexedJobEnqueuer>.Instance, _storage);

    // ------------------------------------------------------------------ A1

    [TestCase("changeStateThrows")]
    [TestCase("writerThrows")]
    public async Task A1_InterruptedAfterCommit_WorkerDoesNotRun_RecoveryRunsSameJobIdOnce(string interruption)
    {
        StartServer();
        IndexedJobEnqueuer enqueuer;
        ServiceProvider? faulty = null;
        if (interruption == "changeStateThrows")
            enqueuer = EnqueuerWith(new ThrowingChangeStateClient(Client), _provider);
        else
        {
            var writer = new Mock<IJobIndexWriter>();
            writer.Setup(w => w.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(),
                    It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("JobIndex down"));
            var services = new ServiceCollection();
            services.AddScoped(_ => writer.Object);
            faulty = services.BuildServiceProvider();
            enqueuer = EnqueuerWith(Client, faulty);
        }

        string id;
        using (faulty)
            id = enqueuer.Enqueue(() => PubProbe.Run("a1", null!), Payload());

        State(id)!.Name.ShouldBe("Indexing");
        (await SetRows()).Select(r => r.Value).ShouldBe([id]);
        (await QueuedRows(id)).ShouldBe(0, "a worker must not run a job that was never published");
        PubProbe.Runs.ShouldNotContainKey(id, "a worker must not run a job that was never published");
        (await JobCount()).ShouldBe(1);

        using var scope = _provider.CreateScope();
        var writerReal = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
        var summary = await NewService(writerReal, TimeAt(TimeSpan.FromMinutes(3))).RunPassAsync(_storage, Client, default);
        TestContext.Out.WriteLine(summary.ToString());
        summary.Published.ShouldBe(1);

        await PubProbe.WhenRun(id);
        await WaitUntil(async () => (await Row(id))?.CurrentState == "Succeeded", "Succeeded JobIndex row");
        // Succeeded is final and the job left the queue: nothing can run it again
        await WaitUntil(async () => await QueuedRows(id) == 0, "queue row of the finished job removed");
        State(id)!.Name.ShouldBe("Succeeded");
        PubProbe.Runs[id].ShouldBe(1);
        PubProbe.Checks[id].ShouldBe("", "parameters and JobIndex row must exist before the job executes");
        PubProbe.Runs.Count.ShouldBe(1);
        (await JobCount()).ShouldBe(1, "recovery must reuse the original job, never create a replacement");
        (await SetRows()).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ A2

    [Test]
    public async Task A2_ScheduledViaRecovery_PastDueIsDispatched_FutureKeepsAbsoluteTime()
    {
        StartServer();
        var now = DateTime.UtcNow;
        var pastDue = IndexedJobPublicationRecord.ForScheduled(Payload("past"), now.AddMinutes(-10), TimeSpan.FromMinutes(1));
        var future = IndexedJobPublicationRecord.ForScheduled(Payload("future"), now.AddMinutes(-5), TimeSpan.FromMinutes(30));
        var pastId = Client.Create(ProbeJob("past"), new IndexingState(pastDue));
        var futureId = Client.Create(ProbeJob("future"), new IndexingState(future));
        (await SetRows()).Select(r => r.Value).ShouldBe([pastId, futureId], ignoreOrder: true);

        using var scope = _provider.CreateScope();
        var summary = await NewService(scope.ServiceProvider.GetRequiredService<IJobIndexWriter>(), TimeAt(TimeSpan.Zero))
            .RunPassAsync(_storage, Client, default);
        TestContext.Out.WriteLine(summary.ToString());
        summary.Published.ShouldBe(2);

        // Future: Scheduled at the absolute time computed at creation (now+25 min, not now+30 min).
        var data = State(futureId)!;
        data.Name.ShouldBe("Scheduled");
        var enqueueAt = JobHelper.DeserializeDateTime(data.Data["EnqueueAt"]);
        enqueueAt.ShouldBe(future.EnqueueAtUtc!.Value, TimeSpan.FromSeconds(1));
        (enqueueAt - DateTime.UtcNow).TotalMinutes.ShouldBeInRange(24.0, 26.0);

        // Past-due: restored as past-due (not pushed forward) and dispatched by the scheduler.
        var history = _storage.GetMonitoringApi().JobDetails(pastId).History;
        var scheduled = history.Single(h => h.StateName == "Scheduled");
        JobHelper.DeserializeDateTime(scheduled.Data["EnqueueAt"]).ShouldBe(pastDue.EnqueueAtUtc!.Value, TimeSpan.FromSeconds(1));
        await PubProbe.WhenRun(pastId);
        PubProbe.Checks[pastId].ShouldBe("");

        // The scheduler only moves due members (score up to now); the future one is 25 minutes
        // ahead: still Scheduled, never queued, so no worker can run it
        State(futureId)!.Name.ShouldBe("Scheduled");
        (await QueuedRows(futureId)).ShouldBe(0);
        PubProbe.Runs.ShouldNotContainKey(futureId);
        PubProbe.Runs[pastId].ShouldBe(1);
        (await JobCount()).ShouldBe(2);
        (await SetRows()).ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ A6

    private sealed class FaultOnIndexing(bool onApplied, bool onUnapplied) : JobFilterAttribute, IApplyStateFilter
    {
        public bool Armed = true;

        public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (Armed && onApplied && context.NewState.Name == "Indexing")
                throw new InvalidOperationException("injected after the Indexing handler");
        }

        public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
        {
            if (Armed && onUnapplied && context.OldStateName == "Indexing")
                throw new InvalidOperationException("injected while leaving Indexing");
        }
    }

    [Test]
    public async Task A6_FaultAroundPreparationCommit_NoLiveCandidateFromAbortedTransaction()
    {
        var fault = new FaultOnIndexing(onApplied: true, onUnapplied: false);
        GlobalJobFilters.Filters.Add(fault);
        for (var i = 0; i < 3; i++)
            Should.Throw<Exception>(() => Client.Create(ProbeJob(), new IndexingState(
                IndexedJobPublicationRecord.ForImmediate(Payload(), DateTime.UtcNow.AddMinutes(-10), "pq"))));
        GlobalJobFilters.Filters.Remove(fault);

        (await SetRows()).ShouldBeEmpty();
        (await Scalar("Job", "`StateName`='Indexing'")).ShouldBe(0);
        using var scope = _provider.CreateScope();
        var summary = await NewService(scope.ServiceProvider.GetRequiredService<IJobIndexWriter>(),
            TimeAt(TimeSpan.FromDays(1))).RunPassAsync(_storage, Client, default);
        summary.Examined.ShouldBe(0, "an aborted preparation transaction leaves nothing for recovery to find");
    }

    [Test]
    public async Task A6_CommittedIndexingJobAlwaysHasMember_EvenWithoutJobIndexRow_AndLeavingRemovesIt()
    {
        var ids = Enumerable.Range(0, 4).Select(i => Client.Create(ProbeJob(), new IndexingState(
            IndexedJobPublicationRecord.ForImmediate(Payload("ds" + i), DateTime.UtcNow, "pq")))).ToList();
        (await SetRows()).Select(r => r.Value).ShouldBe(ids, ignoreOrder: true);
        foreach (var id in ids) (await Row(id)).ShouldBeNull("no JobIndex row was ever written");

        // A transaction leaving Indexing that aborts keeps both the state and the member.
        var fault = new FaultOnIndexing(onApplied: false, onUnapplied: true);
        GlobalJobFilters.Filters.Add(fault);
        Should.Throw<Exception>(() => Client.ChangeState(ids[0], new EnqueuedState("pq"), "Indexing"));
        GlobalJobFilters.Filters.Remove(fault);
        State(ids[0])!.Name.ShouldBe("Indexing");
        (await SetRows()).Select(r => r.Value).ShouldContain(ids[0]);

        Client.ChangeState(ids[0], new EnqueuedState("pq"), "Indexing").ShouldBeTrue();
        Client.ChangeState(ids[1], new ScheduledState(DateTime.UtcNow.AddHours(1)), "Indexing").ShouldBeTrue();
        Client.ChangeState(ids[2], new DeletedState { Reason = "test" }, "Indexing").ShouldBeTrue();
        var remaining = (await SetRows()).Select(r => r.Value).ToList();
        remaining.ShouldBe([ids[3]], "only the job still in Indexing keeps a member");
    }

    // ------------------------------------------------------------------ A7

    [Test]
    public async Task A7_MoreThanOneHundredDueMembers_TwoPassesDrain_FailingMembersDoNotStarveOthers()
    {
        const int total = 130, failingCount = 10;
        var created = DateTime.UtcNow.AddMinutes(-10);
        var ids = new List<string>();
        for (var i = 0; i < total; i++)
            ids.Add(Client.Create(ProbeJob(), new IndexingState(
                IndexedJobPublicationRecord.ForImmediate(Payload("d" + (i % 5)), created, "pq"))));
        (await SetRows()).Count.ShouldBe(total);
        var failing = ids.Take(failingCount).ToHashSet();

        using var scope = _provider.CreateScope();
        var real = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
        var wrapper = new Mock<IJobIndexWriter>();
        wrapper.Setup(w => w.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(),
                It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, IndexPayload, DateTime, string?, CancellationToken>((id, meta, at, display, ct) =>
                failing.Contains(id)
                    ? throw new InvalidOperationException("JobIndex rejects " + id)
                    : real.PrepareForPublicationAsync(id, meta, at, display, ct));
        var time = TimeAt(TimeSpan.Zero);
        var service = NewService(wrapper.Object, time);

        var first = await service.RunPassAsync(_storage, Client, default);
        TestContext.Out.WriteLine("pass 1: " + first);
        first.Examined.ShouldBe(IndexedJobPublicationRecoveryService.MaxCandidatesPerPass);
        first.StopReason.ShouldBe(RecoveryStopReason.CandidateLimit);

        var second = await service.RunPassAsync(_storage, Client, default);
        TestContext.Out.WriteLine("pass 2: " + second);
        second.StopReason.ShouldBe(RecoveryStopReason.Drained);
        (first.Examined + second.Examined).ShouldBe(total, "each member is examined exactly once; none is returned twice");
        (first.Published + second.Published).ShouldBe(total - failingCount);
        (first.Deferred + second.Deferred).ShouldBe(failingCount);

        foreach (var id in ids.Skip(failingCount)) State(id)!.Name.ShouldBe("Enqueued");
        foreach (var id in failing) State(id)!.Name.ShouldBe("Indexing");
        var rows = await SetRows();
        rows.Select(r => r.Value).ToHashSet().SetEquals(failing).ShouldBeTrue();
        var bound = IndexingStateHandler.ScoreForNow(time.Now);
        rows.ShouldAllBe(r => r.Score > bound, "failing members were upserted to a future score");

        var third = await service.RunPassAsync(_storage, Client, default);
        third.Examined.ShouldBe(0);
        (await SetRows()).Count.ShouldBe(failingCount);
    }

    [Test]
    public async Task A7_MalformedRecord_OnMySql_IsDeletedWithReason_RowMarkedWithErrorType()
    {
        var id = Client.Create(ProbeJob(), new RawIndexingState(new Dictionary<string, string>
        {
            [IndexedJobPublicationRecord.SchemaVersionKey] = "1",
            [IndexedJobPublicationRecord.RecordKey] = "{ this is not json"
        }));
        (await SetRows()).Select(r => r.Value).ShouldBe([id]);

        using var scope = _provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
        await writer.UpsertOnEnqueueAsync(id, Payload(), DateTime.UtcNow.AddMinutes(-9), "m");

        StartServer();
        var summary = await NewService(writer, TimeAt(TimeSpan.FromMinutes(5))).RunPassAsync(_storage, Client, default);
        TestContext.Out.WriteLine(summary.ToString());
        summary.Cancelled.ShouldBe(1);

        var state = State(id)!;
        state.Name.ShouldBe("Deleted");
        state.Reason.ShouldNotBeNull();
        state.Reason!.ShouldContain("Invalid publication record");
        var row = (await Row(id))!;
        row.CurrentState.ShouldBe("Deleted");
        row.ErrorType.ShouldBe("InvalidPublicationRecord");
        (await SetRows()).ShouldBeEmpty();
        // Deleted, and never queued: no worker can run it
        (await QueuedRows(id)).ShouldBe(0);
        PubProbe.Runs.ShouldNotContainKey(id);
        using var c = _storage.GetConnection();
        c.GetJobParameter(id, JobParamKeys.OrgSlug).ShouldBeNull("nothing is published for an invalid record");
    }
}

internal sealed class ExceptionCapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var text = $"{logLevel}] {formatter(state, exception)}" +
                   (exception == null ? "" : " :: " + exception.ToString().Split('\n').Take(4).Aggregate((a, b) => a + " | " + b));
        lock (Entries) Entries.Add(text);
    }
}
