#nullable enable
using Registry.Adapters;
using Registry.Web.Services.Jobs;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Distributed;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Console;
using Hangfire.MySql;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MySqlConnector;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;
using Registry.Test.Common;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Builds;

namespace Registry.Web.Test;

/// <summary>
/// Joint native/Registry regressions using the shipped MySQL Hangfire provider,
/// four real workers, serialized DDB arguments, a real JobIndex and CacheManager.
/// MySQL is the shared MariaDB test server (<see cref="MariaDbServer"/>, Docker);
/// the freshly built libddb must be on the native loader path. Each test owns only
/// its random table prefix and temporary dataset directory.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class PendingRecoveryIntegrationTests
{
    private const string Org = "joint-recovery";
    private string _connection = null!;
    private string _prefix = null!;
    private string _root = null!;
    private MySqlStorage _storage = null!;
    private ServiceProvider _provider = null!;
    private BackgroundJobServer? _server;
    private JobStorage? _previousStorage;
    private JobActivator _previousActivator = null!;
    private JobFilter[] _previousFilters = [];
    private Hangfire.States.IStateHandler[] _previousStateHandlers = [];
    private static JobFilter[] _consoleFilters = [];
    private readonly ConcurrentDictionary<Guid, IDDB> _datasets = new();
    private readonly WorkerGate _gate = new();
    private IBackgroundJobClient Client => _provider.GetRequiredService<IBackgroundJobClient>();
    private IBackgroundJobsProcessor Jobs => _provider.GetRequiredService<IBackgroundJobsProcessor>();
    private ICacheManager Cache => _provider.GetRequiredService<ICacheManager>();
    private NativeEnvironmentScope _environment = null!;
    private bool _initialized;

    [SetUp]
    public async Task SetUp()
    {
        _connection = (await MariaDbServer.GetAsync()).ConnectionString;

        _root = Path.Combine(Path.GetTempPath(), "registry-joint-" + Guid.NewGuid().ToString("N"));
        _prefix = "joint" + Guid.NewGuid().ToString("N")[..12];
        Directory.CreateDirectory(_root);
        _datasets.Clear();
        _gate.Reset();
        _previousActivator = JobActivator.Current;
        _previousFilters = GlobalJobFilters.Filters.ToArray();
        _previousStateHandlers = GlobalStateHandlers.Handlers.ToArray();
        try { _previousStorage = JobStorage.Current; }
        catch (InvalidOperationException) { _previousStorage = null; }
        _environment = new NativeEnvironmentScope("DDB_MIN_FREE_DISK_MB", "DDB_OBJ2TILES_PATH");
        _environment.Set("DDB_MIN_FREE_DISK_MB", "0");
        // Tiles are optional here; the real bundled converter is exercised by
        // the native regression suite. Nexus remains a real native build.
        _environment.Set("DDB_OBJ2TILES_PATH", Path.Combine(_root, "missing-Obj2Tiles"));
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
        var manager = new Mock<IDdbManager>();
        manager.Setup(x => x.Get(Org, It.IsAny<Guid>()))
            .Returns<string, Guid>((_, id) => _datasets[id]);
        manager.Setup(x => x.Open(Org, It.IsAny<Guid>()))
            .Returns<string, Guid>((_, id) => _datasets[id]);
        manager.Setup(x => x.TryParseDatasetFolder(It.IsAny<string>(), out It.Ref<string>.IsAny, out It.Ref<Guid>.IsAny))
            .Returns(new TryParseFolder((string? folder, out string org, out Guid id) =>
            {
                id = _datasets.FirstOrDefault(d => d.Value.DatasetFolderPath == folder).Key;
                org = id == Guid.Empty ? string.Empty : Org;
                return id != Guid.Empty;
            }));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache().AddRecordCache();
        services.AddSingleton<ICacheKeyScanner, NullCacheKeyScanner>();
        services.AddSingleton<ICacheManager, CacheManager>();
        services.AddSingleton(manager.Object);
        services.AddSingleton<JobStorage>(_storage);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new System.Collections.Generic.Dictionary<string, string?>
            {
                ["ConnectionStrings:" + MagicStrings.HangfireConnectionName] = _connection
            }).Build());
        services.AddSingleton<IBackgroundJobClient>(new BackgroundJobClient(_storage));
        services.AddSingleton<IIndexedJobEnqueuer, IndexedJobEnqueuer>();
        services.AddSingleton<IBackgroundJobsProcessor, BackgroundJobsProcessor>();
        services.AddDbContext<RegistryContext>(options =>
            options.UseSqlite("Data Source=" + Path.Combine(_root, "registry.sqlite")));
        services.AddScoped<IJobIndexWriter, JobIndexWriter>();
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DatasetSweep>();
        services.AddScoped<PendingBuildEnqueuer>();
        services.AddScoped<DdbJobs>();
        services.AddScoped<BuildPendingService>();
        services.AddScoped<ArtifactCompletenessCheckerService>();
        services.AddSingleton<IDdbWrapper, NativeDdbWrapper>().AddBuildScheduling();

        // Each invocation queries the real index after the production wrapper
        // has acquired its dataset lock. Only the barrier is test-controlled.
        var query = new Mock<IJobIndexQuery>();
        query.Setup(x => x.GetByOrgDsAsync(It.IsAny<string>(), It.IsAny<string>(), 0, int.MaxValue,
                It.IsAny<CancellationToken>()))
            .Returns<string, string, int, int, CancellationToken>(async (org, ds, skip, take, ct) =>
            {
                _gate.BeforeSnapshot(ds);
                using var scope = _provider.CreateScope();
                return await new JobIndexQuery(scope.ServiceProvider.GetRequiredService<RegistryContext>())
                    .GetByOrgDsAsync(org, ds, skip, take, ct);
            });
        // The completeness sweep asks which entries already have a build job: the real index answers
        query.Setup(x => x.GetActivePathsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<System.Collections.Generic.IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, string, System.Collections.Generic.IReadOnlyCollection<string>, CancellationToken>(
                async (org, ds, tool, paths, ct) =>
                {
                    using var scope = _provider.CreateScope();
                    return await new JobIndexQuery(scope.ServiceProvider.GetRequiredService<RegistryContext>())
                        .GetActivePathsAsync(org, ds, tool, paths, ct);
                });
        services.AddSingleton(query.Object);
        services.AddSingleton(TestJobs.Budget());
        _provider = services.BuildServiceProvider();
        Cache.Register(MagicStrings.BuildPendingTrackerCacheSeed, _ => Task.FromResult(Encoding.UTF8.GetBytes("{}")));
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RegistryContext>().Database.EnsureCreatedAsync();

        GlobalJobFilters.Filters.Clear();
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UseActivator(new ProviderActivator(_provider));
        if (_consoleFilters.Length == 0)
        {
            GlobalConfiguration.Configuration.UseConsole();
            _consoleFilters = GlobalJobFilters.Filters.Where(x =>
                x.Instance.GetType().Assembly.GetName().Name == "Hangfire.Console").ToArray();
        }
        else
        {
            foreach (var filter in _consoleFilters) GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        }
        GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 1 });
        IndexingStateHandler.EnsureRegistered();
        GlobalJobFilters.Filters.Add(new JobIndexStateFilter(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JobIndexStateFilter>>()));
        InstallFailureFilters(failureFirst: true);
        GlobalJobFilters.Filters.Add(_gate);
        var native = new NativeDdbWrapper();
        TestContext.Progress.WriteLine($"Native artifact: {native.GetVersion()}; MySQL prefix: {_prefix}; workers: 4");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (!_initialized) return;
        _gate.ReleaseSnapshots.Set();
        _gate.ReleaseEntries.Set();
        _server?.Dispose();
        _server = null;
        _provider?.Dispose();
        GlobalJobFilters.Filters.Clear();
        foreach (var filter in _previousFilters)
            GlobalJobFilters.Filters.Add(filter.Instance, filter.Order);
        GlobalStateHandlers.Handlers.Clear();
        foreach (var handler in _previousStateHandlers) GlobalStateHandlers.Handlers.Add(handler);
        GlobalConfiguration.Configuration.UseActivator(_previousActivator);
        if (_previousStorage != null) JobStorage.Current = _previousStorage;
        _environment.Dispose();

        // Drop only the exact random prefix owned by this test. Never drop the
        // supplied database or use a broad LIKE pattern that could match others.
        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        var tables = new System.Collections.Generic.List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0);
                if (table.StartsWith(_prefix, StringComparison.Ordinal)) tables.Add(table);
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SET FOREIGN_KEY_CHECKS=0";
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            foreach (var table in tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE `" + table.Replace("`", "``") + "`";
                await command.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET FOREIGN_KEY_CHECKS=1";
            await command.ExecuteNonQueryAsync();
        }
        // Pooled SQLite connections keep registry.sqlite open, which blocks the
        // delete on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        _initialized = false;
    }

    [Test]
    public async Task LateCompanion_DeferredWorkerSchedulesDueEntryAndCompletes()
    {
        var ddb = await Dataset("companion");
        Add(ddb, "model.obj", "mtllib late.mtl\n" + GridObj());
        var initial = StageEntry(ddb, "companion", "model.obj");
        StartWorkers();
        Promote(initial);
        await WaitForState(initial, "Succeeded");
        var marker = ddb.GetPendingBuildInfo().Single();
        marker.Path.ShouldBe("model.obj");
        marker.MissingDependencies.ShouldContain("late.mtl");
        ddb.IsBuildComplete("model.obj").ShouldBeFalse();
        var retry = await WaitForRetry("companion", 1);
        ScheduledDelay(retry).ShouldBe(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2));

        Add(ddb, "late.mtl", "newmtl plain\nKd 0.8 0.6 0.4\n");
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BuildPendingService>()
                .SetDatasetHasPendingBuilds(Org, "companion", true);
        var preserved = await CacheState("companion");
        preserved.GetProperty("RetryAttempt").GetInt32().ShouldBe(1);
        preserved.GetProperty("ScheduledRetryJobId").GetString().ShouldBe(retry);

        MakeDue(ddb, "model.obj");
        Promote(retry);
        await Until(async () => ddb.IsBuildComplete("model.obj") &&
            (await Rows("companion")).Any(x => x.Path == "model.obj" && x.JobId != initial && x.CurrentState == "Succeeded"));
        ddb.IsBuildPending().ShouldBeFalse();
        await Until(async () => (await CacheState("companion")).GetProperty("RetryAttempt").GetInt32() == 0);
        (await Rows("companion")).Single(x => x.JobId == initial).CurrentState.ShouldBe("Succeeded");
    }

    [Test]
    public async Task PendingDueBoundary_BeforeRetryAfterSkipsAndAtRetryAfterQueuesRealEntry()
    {
        var ddb = await PendingModel("boundary");
        Add(ddb, "late.mtl", "newmtl plain\nKd 1 1 1\n");
        var marker = ddb.GetPendingBuildInfo().Single();
        // Libddb reports when it retries the build; the enqueue path follows it
        marker.RetryAfter.ShouldBe(marker.LastAttempt + PendingMarkers.LibddbRetryBackoffSeconds);
        using var scope = _provider.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<IJobIndexQuery>();
        Enqueuer(Jobs, index, marker.RetryAfter - 1).Run(ddb, Org, "boundary", NoConsole);
        (await Rows("boundary")).ShouldBeEmpty();
        Enqueuer(Jobs, index, marker.RetryAfter).Run(ddb, Org, "boundary", NoConsole);
        var entry = (await Rows("boundary")).Single();
        entry.Path.ShouldBe("model.obj");
        StartWorkers();
        await WaitForState(entry.JobId, "Succeeded");
        ddb.IsBuildComplete("model.obj").ShouldBeTrue();
        ddb.IsBuildPending().ShouldBeFalse();
    }

    [Test]
    public async Task OverlappingWorkers_FreshLockedSnapshotsDeduplicateAndOtherDatasetProceeds()
    {
        var ddb = await PendingModel("overlap");
        var independent = await PendingModel("independent");
        Add(ddb, "late.mtl", "newmtl plain\nKd 1 1 1\n");
        Add(independent, "late.mtl", "newmtl plain\nKd 1 1 1\n");
        MakeDue(ddb, "model.obj");
        MakeDue(independent, "model.obj");
        _gate.BlockDataset = "overlap";
        _gate.ReleaseEntries.Reset();
        var first = StageCoordinator(ddb, "overlap");
        var second = StageCoordinator(ddb, "overlap");
        var other = StageCoordinator(independent, "independent");
        StartWorkers();
        Promote(first);
        _gate.FirstSnapshot.Wait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
        Promote(second);
        Promote(other);
        await Until(() => Task.FromResult(_gate.CoordinatorsStarted >= 3));
        await WaitForState(other, "Succeeded");
        // MySqlStorage's table lock lease alone expires after its five-second
        // acquisition horizon. The holder stays blocked until the contender's typed
        // finite retry is observed: the contender reaches Scheduled only when its
        // five-second session-lock wait (GET_LOCK) gives up, which started after the
        // holder took both locks, so the holder is by then past the lease horizon.
        // Then retry against the fresh index.
        await WaitForState(second, "Scheduled");
        (await Rows("independent")).Count(x => x.Path == "model.obj").ShouldBe(1);
        Volatile.Read(ref _gate.BlockedSnapshots).ShouldBe(1);
        _gate.ReleaseSnapshots.Set();
        await WaitForState(first, "Succeeded");
        Promote(second);
        await WaitForState(second, "Succeeded");
        Volatile.Read(ref _gate.BlockedSnapshots).ShouldBe(2);
        var entries = (await Rows("overlap")).Where(x => x.Path == "model.obj").ToArray();
        entries.Length.ShouldBe(1);
        _gate.ReleaseEntries.Set();
        await WaitForState(entries.Single().JobId, "Succeeded");
        ddb.IsBuildComplete("model.obj").ShouldBeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PendingLowDiskFailure_RemainsFailedAndRetriesAfterRecoveryInEitherFilterOrder(bool failureFirst)
    {
        InstallFailureFilters(failureFirst);
        var ddb = await PendingModel("transient");
        Add(ddb, "late.mtl", "newmtl plain\nKd 1 1 1\n");
        MakeDue(ddb, "model.obj");
        _environment.Set("DDB_MIN_FREE_DISK_MB", "9000000000000");
        var failed = StageEntry(ddb, "transient", "model.obj");
        StartWorkers();
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Promote(failed);
        await WaitForState(failed, "Failed");
        var marker = ddb.GetPendingBuildInfo().Single();
        marker.Path.ShouldBe("model.obj");
        marker.LastAttempt.ShouldBeGreaterThanOrEqualTo(before);
        var failedRow = (await Rows("transient")).Single(x => x.JobId == failed);
        failedRow.ErrorType.ShouldNotBeNullOrWhiteSpace();
        var retry = await WaitForRetry("transient", 1);
        ScheduledDelay(retry).ShouldBe(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2));
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BuildPendingService>().ProcessPendingBuilds(null, CancellationToken.None);
        (await CacheState("transient")).GetProperty("RetryAttempt").GetInt32().ShouldBe(1);
        (await CacheState("transient")).GetProperty("ScheduledRetryJobId").GetString().ShouldBe(retry);

        _environment.Set("DDB_MIN_FREE_DISK_MB", "0");
        MakeDue(ddb, "model.obj");
        Promote(retry);
        await Until(async () => ddb.IsBuildComplete("model.obj") &&
            (await Rows("transient")).Any(x => x.Path == "model.obj" && x.JobId != failed && x.CurrentState == "Succeeded"));
        ddb.IsBuildPending().ShouldBeFalse();
        (await Rows("transient")).Single(x => x.JobId == failed).CurrentState.ShouldBe("Failed");
        await Until(async () => (await CacheState("transient")).GetProperty("RetryAttempt").GetInt32() == 0);
    }

    [Test]
    public async Task RepeatedTransientFailure_StopsAtBudgetAndUnchangedChecksumSweepStartsNewEpoch()
    {
        var ddb = await PendingModel("budget");
        Add(ddb, "late.mtl", "newmtl plain\nKd 1 1 1\n");
        var checksum = ddb.GetStamp().Checksum;
        _environment.Set("DDB_MIN_FREE_DISK_MB", "9000000000000");
        var first = StageEntry(ddb, "budget", "model.obj");
        StartWorkers();
        Promote(first);
        await WaitForState(first, "Failed");
        var delays = new[] { 1, 2, 5, 10, 20, 40, 60, 60, 60, 60, 60, 60 };
        var epoch = (await CacheState("budget")).GetProperty("RetryEpoch").GetString();
        for (var attempt = 1; attempt <= delays.Length; attempt++)
        {
            var retry = await WaitForRetry("budget", attempt);
            ScheduledDelay(retry).ShouldBe(TimeSpan.FromMinutes(delays[attempt - 1]), TimeSpan.FromSeconds(2));
            var previousEntries = (await Rows("budget")).Count(x => x.Path == "model.obj");
            MakeDue(ddb, "model.obj");
            Promote(retry);
            await WaitForState(retry, "Succeeded");
            await Until(async () =>
            {
                var failedEntries = (await Rows("budget")).Where(x => x.Path == "model.obj" && x.CurrentState == "Failed").ToArray();
                if (failedEntries.Length != previousEntries + 1) return false;
                using var connection = _storage.GetConnection();
                // JobIndex stamps happen inside OnStateApplied, before Hangfire
                // commits Failed. Promote the next retry only after that commit;
                // otherwise a correctly active snapshot may skip the prior job.
                return failedEntries.All(x => connection.GetStateData(x.JobId)?.Name == "Failed");
            });
        }
        await Until(async () => (await CacheState("budget")).GetProperty("ScheduledRetryJobId").ValueKind == JsonValueKind.Null);
        var exhausted = await CacheState("budget");
        exhausted.GetProperty("RetryAttempt").GetInt32().ShouldBe(12);
        exhausted.GetProperty("RetryEpoch").GetString().ShouldBe(epoch);
        ddb.GetStamp().Checksum.ShouldBe(checksum);
        var count = (await Rows("budget")).Length;
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BuildPendingService>().ScheduleRetryIfPending(Org, "budget", null, null, null);
        (await Rows("budget")).Length.ShouldBe(count);

        // Hold the restarted entry until the sweep has published its epoch;
        // otherwise the real worker may finish before the assertion reads it.
        _gate.ReleaseEntries.Reset();
        MakeDue(ddb, "model.obj");
        _environment.Set("DDB_MIN_FREE_DISK_MB", "0");
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BuildPendingService>().ProcessPendingBuilds(null, CancellationToken.None);
        var restarted = await CacheState("budget");
        restarted.GetProperty("RetryEpoch").GetString().ShouldNotBe(epoch);
        // The coordinator's real OnStateApplied may already schedule attempt 1.
        restarted.GetProperty("RetryAttempt").GetInt32().ShouldBeLessThanOrEqualTo(1);
        _gate.ReleaseEntries.Set();
        await Until(() => Task.FromResult(ddb.IsBuildComplete("model.obj")));
        await Until(async () => (await CacheState("budget")).GetProperty("RetryAttempt").GetInt32() == 0);
        (await Rows("budget")).Single(x => x.JobId == first).CurrentState.ShouldBe("Failed");
    }

    [Test]
    public async Task PermanentDependencyEscape_ConsumesOwnMarkerAndOtherPendingEntryDoesNotTriggerRetry()
    {
        var ddb = await PendingModel("permanent");
        Add(ddb, "other.obj", "mtllib never.mtl\n" + GridObj());
        ddb.Build("other.obj");
        ddb.GetPendingBuildInfo().Length.ShouldBe(2);
        Add(ddb, "late.mtl", "newmtl plain\nmap_Kd ../../outside.png\n");
        var job = StageEntry(ddb, "permanent", "model.obj");
        StartWorkers();
        Promote(job);
        await WaitForState(job, "Failed");
        var remaining = ddb.GetPendingBuildInfo().Single();
        remaining.Path.ShouldBe("other.obj");
        remaining.Hash.ShouldNotBe(ddb.GetEntry("model.obj")!.Hash);
        var rows = await Rows("permanent");
        rows.Length.ShouldBe(1);
        rows.Single().ErrorType.ShouldNotBeNullOrWhiteSpace();
        (await CacheState("permanent")).GetProperty("ScheduledRetryJobId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task CompletenessSweep_RecoversOldTilesThroughForceFalseAndPreservesNexus()
    {
        var ddb = await Dataset("tiles");
        Add(ddb, "recover.obj", GridObj());
        Add(ddb, "ordinary.obj", GridObj() + "# independent hash\n");
        ddb.Build("recover.obj");
        ddb.Build("ordinary.obj");
        var hash = ddb.GetEntry("recover.obj")!.Hash;
        var root = Path.Combine(ddb.DerivedFolderPaths.Single(), hash);
        var nexus = Path.Combine(root, "nxs", "model.nxz");
        var bytes = await File.ReadAllBytesAsync(nexus);
        var modified = File.GetLastWriteTimeUtc(nexus);
        foreach (var path in new[] { "recover.obj", "ordinary.obj" })
        {
            var tiles = Path.Combine(ddb.DerivedFolderPaths.Single(), ddb.GetEntry(path)!.Hash, "3dtiles");
            if (Directory.Exists(tiles)) Directory.Delete(tiles, recursive: true);
        }
        var old = Path.Combine(root, "3dtiles-old");
        Directory.CreateDirectory(old);
        const string tileset = "{\"asset\":{\"version\":\"1.0\"},\"geometricError\":0,\"root\":{\"content\":{\"uri\":\"old.b3dm\"}}}";
        await File.WriteAllTextAsync(Path.Combine(old, "tileset.json"), tileset);
        await File.WriteAllBytesAsync(Path.Combine(old, "old.b3dm"), [1, 2, 3, 4]);
        ddb.IsBuildComplete("recover.obj").ShouldBeFalse();
        ddb.IsBuildComplete("ordinary.obj").ShouldBeTrue();
        using (var scope = _provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ArtifactCompletenessCheckerService>().CheckAndQueueAsync(null, CancellationToken.None);
        var row = (await Rows("tiles")).Single();
        row.Path.ShouldBe("recover.obj");
        using (var connection = _storage.GetConnection())
            connection.GetJobData(row.JobId).Job.Args[3].ShouldBe(false); // DdbJobs.Build(org, ref, path, force, ctx)
        StartWorkers();
        await WaitForState(row.JobId, "Succeeded");
        ddb.IsBuildComplete("recover.obj").ShouldBeTrue();
        (await File.ReadAllBytesAsync(nexus)).ShouldBe(bytes);
        File.GetLastWriteTimeUtc(nexus).ShouldBe(modified);
        (await File.ReadAllTextAsync(Path.Combine(root, "3dtiles", "tileset.json"))).ShouldBe(tileset);
        File.Exists(Path.Combine(root, "3dtiles", "old.b3dm")).ShouldBeTrue();
        Directory.Exists(old).ShouldBeFalse();
    }

    [Test]
    public async Task SeparateHostCachesAndCategoryInvalidationShareAuthoritativeMySqlBudget()
    {
        const string ds = "shared-controls";
        await PendingModel(ds);
        await Cache.SetAsync(MagicStrings.BuildPendingTrackerCacheSeed, CacheCategories.ForDataset(Org, ds),
            JsonSerializer.SerializeToUtf8Bytes(new { HasPending = true, RetryAttempt = 3, RetryEpoch = "legacy-epoch" }));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache().AddRecordCache();
        services.AddSingleton<ICacheKeyScanner, NullCacheKeyScanner>();
        services.AddSingleton<ICacheManager, CacheManager>();
        services.AddSingleton(_provider.GetRequiredService<IDdbManager>());
        services.AddSingleton<JobStorage>(_storage);
        services.AddSingleton(_provider.GetRequiredService<IConfiguration>());
        services.AddSingleton(Jobs);
        services.AddDbContext<RegistryContext>(options =>
            options.UseSqlite("Data Source=" + Path.Combine(_root, "registry.sqlite")));
        services.AddScoped<DatasetSweep>();
        services.AddScoped<BuildPendingService>();
        using var secondHost = services.BuildServiceProvider();
        var secondCache = secondHost.GetRequiredService<ICacheManager>();
        secondCache.Register(MagicStrings.BuildPendingTrackerCacheSeed, _ => Task.FromResult(Encoding.UTF8.GetBytes("{}")));
        using var firstScope = _provider.CreateScope();
        using var secondScope = secondHost.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<BuildPendingService>();
        var second = secondScope.ServiceProvider.GetRequiredService<BuildPendingService>();
        // The first upgraded host has an empty memory cache. It must leave
        // migration open for the other host's populated legacy observation.
        await second.SetDatasetHasPendingBuilds(Org, ds, true);
        async Task InvalidateFirstHost()
        {
            var distributed = _provider.GetRequiredService<IDistributedCache>();
            var scanner = new Mock<ICacheKeyScanner>();
            scanner.Setup(x => x.RemoveByPatternAsync(It.IsAny<string>())).Returns<string>(async pattern =>
            {
                await distributed.RemoveAsync(pattern.TrimEnd('*'));
                return 1;
            });
            var invalidatingCache = new CacheManager(distributed,
                _provider.GetRequiredService<ILogger<CacheManager>>(), scanner.Object);
            invalidatingCache.Register(MagicStrings.BuildPendingTrackerCacheSeed,
                _ => Task.FromResult(Encoding.UTF8.GetBytes("{}")));
            await new DatasetCacheInvalidator(_provider.GetRequiredService<ILogger<DatasetCacheInvalidator>>(),
                invalidatingCache, Mock.Of<IOgcCacheKeys>(), _storage, _provider.GetRequiredService<IConfiguration>())
                .InvalidateAllDatasetCachesAsync(Org, ds);
        }
        // The first invalidation migrates the still-populated legacy cache
        // before evicting it, even though the empty host initialized the hash.
        await InvalidateFirstHost();
        await first.ScheduleRetryIfPending(Org, ds, null, null, null);
        await second.ScheduleRetryIfPending(Org, ds, null, null, null);
        var coordinator = (await Rows(ds)).Single();
        ScheduledDelay(coordinator.JobId).ShouldBe(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(2));
        (await CacheState(ds)).GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
        using (var snapshot = JsonDocument.Parse(await secondCache.GetAsync(MagicStrings.BuildPendingTrackerCacheSeed,
                   CacheCategories.ForDataset(Org, ds))))
            snapshot.RootElement.GetProperty("RetryAttempt").GetInt32().ShouldBe(4);

        // Remove the upgraded mirror too; the live shared controls remain intact.
        await InvalidateFirstHost();
        using (var empty = JsonDocument.Parse(await Cache.GetAsync(MagicStrings.BuildPendingTrackerCacheSeed,
                   CacheCategories.ForDataset(Org, ds))))
            empty.RootElement.TryGetProperty("RetryAttempt", out _).ShouldBeFalse();
        await first.ScheduleRetryIfPending(Org, ds, null, null, null);
        (await Rows(ds)).Length.ShouldBe(1);
        (await CacheState(ds)).GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
        Client.ChangeState(coordinator.JobId, new DeletedState { Reason = "Test completed coordinator" }, "Scheduled").ShouldBeTrue();
        await second.ScheduleRetryIfPending(Org, ds, null, null, null);
        await first.ScheduleRetryIfPending(Org, ds, null, null, null);
        (await Rows(ds)).Length.ShouldBe(2);
        (await CacheState(ds)).GetProperty("RetryAttempt").GetInt32().ShouldBe(5);
        using var connection = _storage.GetConnection();
        using var shared = JsonDocument.Parse(connection.GetAllEntriesFromHash(DatasetBuildCoordinator.RetryStateKey(Org, ds))["Controls"]);
        shared.RootElement.GetProperty("RetryAttempt").GetInt32().ShouldBe(5);
        shared.RootElement.GetProperty("RetryEpoch").GetString().ShouldBe("legacy-epoch");
    }

    [Test]
    public async Task PublicationBarrierPreventsEarlyWorkerFailureAndRetainsFailedIndexForRecovery()
    {
        const string ds = "publication";
        var ddb = await PendingModel(ds);
        Add(ddb, "late.mtl", "newmtl plain\nKd 1 1 1\n");
        MakeDue(ddb, "model.obj");
        _environment.Set("DDB_MIN_FREE_DISK_MB", "9000000000000");
        using var beforeIndex = new ManualResetEventSlim();
        using var releaseIndex = new ManualResetEventSlim();
        string? stagedId = null;
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(x => x.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, IndexPayload, DateTime, string, CancellationToken>(async (id, meta, created, method, ct) =>
            {
                stagedId = id;
                beforeIndex.Set();
                if (!releaseIndex.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Publication barrier not released");
                using var scope = _provider.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<IJobIndexWriter>()
                    .PrepareForPublicationAsync(id, meta, created, method, ct);
            });
        using var publicationProvider = new ServiceCollection().AddSingleton(writer.Object).BuildServiceProvider();
        var indexed = new IndexedJobEnqueuer(Client, publicationProvider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<IndexedJobEnqueuer>>(), _storage);
        var jobs = new BackgroundJobsProcessor(Client, indexed, _storage);
        StartWorkers();
        var coordinator = Task.Run(() => Enqueuer(jobs, _provider.GetRequiredService<IJobIndexQuery>(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Run(ddb, Org, ds, NoConsole));
        try
        {
            beforeIndex.Wait(TimeSpan.FromSeconds(15)).ShouldBeTrue();
            using (var connection = _storage.GetConnection())
            {
                connection.GetStateData(stagedId).Name.ShouldBe(IndexedJobEnqueuer.PreparationStateName);
                connection.GetJobParameter(stagedId, JobParamKeys.Path).ShouldBe("model.obj");
            }
            // Staged in the preparation state, which is not a queue: no worker can fetch the job
            // (so none can fail it early) while publication is held at the barrier
            (await Rows(ds)).ShouldBeEmpty();
        }
        finally { releaseIndex.Set(); }
        await coordinator;
        await WaitForState(stagedId!, "Failed");
        var failed = (await Rows(ds)).Single(x => x.JobId == stagedId);
        failed.CurrentState.ShouldBe("Failed");
        // The low-disk failure is typed (libddb's reason "lowDiskSpace"), so the completeness
        // checker treats it as transient; before libddb reported reasons it was a generic InvalidOperationException
        failed.ErrorType.ShouldBe(nameof(DdbRetryableException));
        var retry = await WaitForRetry(ds, 1);
        _environment.Set("DDB_MIN_FREE_DISK_MB", "0");
        MakeDue(ddb, "model.obj");
        Promote(retry);
        await Until(async () => ddb.IsBuildComplete("model.obj") &&
            (await Rows(ds)).Any(x => x.Path == "model.obj" && x.JobId != stagedId && x.CurrentState == "Succeeded"));
        var retained = (await Rows(ds)).Single(x => x.JobId == stagedId);
        retained.CurrentState.ShouldBe("Failed");
        retained.ErrorType.ShouldBe(nameof(DdbRetryableException));
    }

    private void InstallFailureFilters(bool failureFirst)
    {
        foreach (var filter in GlobalJobFilters.Filters.ToArray())
            if (filter.Instance is BuildJobFailureFilter or BuildRetrySchedulerFilter)
                GlobalJobFilters.Filters.Remove(filter.Instance);
        var failure = new BuildJobFailureFilter(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BuildJobFailureFilter>>());
        var retry = new BuildRetrySchedulerFilter(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BuildRetrySchedulerFilter>>());
        GlobalJobFilters.Filters.Add(failure, failureFirst ? 1 : 2);
        GlobalJobFilters.Filters.Add(retry, failureFirst ? 2 : 1);
    }

    private void StartWorkers() => _server = new BackgroundJobServer(new BackgroundJobServerOptions
    {
        WorkerCount = 4,
        SchedulePollingInterval = TimeSpan.FromHours(1),
        ShutdownTimeout = TimeSpan.FromSeconds(15),
        ServerName = "joint-" + _prefix
    }, _storage);

    private async Task<IDDB> Dataset(string slug)
    {
        var path = Path.Combine(_root, slug);
        Directory.CreateDirectory(path);
        var ddb = new DDB(path, new NativeDdbWrapper());
        ddb.Init();
        var id = Guid.NewGuid();
        _datasets[id] = ddb;
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = await db.Organizations.SingleOrDefaultAsync(x => x.Slug == Org);
        if (org == null)
        {
            org = new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow };
            db.Organizations.Add(org);
        }
        db.Datasets.Add(new Dataset { Slug = slug, InternalRef = id, CreationDate = DateTime.UtcNow,
            Organization = org, FileTypes = [] });
        await db.SaveChangesAsync();
        return ddb;
    }

    private async Task<IDDB> PendingModel(string slug)
    {
        var ddb = await Dataset(slug);
        Add(ddb, "model.obj", "mtllib late.mtl\n" + GridObj());
        ddb.Build("model.obj");
        ddb.GetPendingBuildInfo().Single().Path.ShouldBe("model.obj");
        return ddb;
    }

    private static void Add(IDDB ddb, string path, string text)
    {
        // DDB.Add(stream) treats its source as temporary and deletes it. Native
        // builds require a persistent dataset source, as Registry materializes.
        File.WriteAllText(Path.Combine(ddb.DatasetFolderPath, path), text);
        ddb.AddRaw(path);
    }

    private Guid IdOf(IDDB ddb) => _datasets.Single(d => ReferenceEquals(d.Value, ddb)).Key;

    private delegate bool TryParseFolder(string? folder, out string orgSlug, out Guid internalRef);

    // The production enqueue path with the clock at unixSeconds and the MySQL coordinator lock
    private PendingBuildEnqueuer Enqueuer(IBackgroundJobsProcessor jobs, IJobIndexQuery index, long unixSeconds) =>
        new(jobs, index, _provider.GetRequiredService<IDdbManager>(), _storage,
            new FakeTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime),
            _provider.GetRequiredService<IConfiguration>());

    private static JobConsole NoConsole => JobConsole.For(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, null);

    private string StageEntry(IDDB ddb, string ds, string path) => Jobs.ScheduleIndexed<DdbJobs>(
        j => j.Build(Org, IdOf(ddb), path, false, null),
        new IndexPayload(Org, ds, ddb.GetEntry(path)!.Hash, MagicStrings.AutoBuildServiceUserId, Path: path),
        TimeSpan.FromHours(1));

    private string StageCoordinator(IDDB ddb, string ds) => Jobs.ScheduleIndexed<DdbJobs>(
        j => j.BuildPending(Org, IdOf(ddb), null),
        new IndexPayload(Org, ds, null, MagicStrings.AutoBuildServiceUserId), TimeSpan.FromHours(1));

    // Assert the actual production ScheduledState first; then accelerate only
    // test jobs, without changing the production backoff table or native clock.
    private void Promote(string id) => Client.ChangeState(id, new EnqueuedState(), "Scheduled").ShouldBeTrue();

    private TimeSpan ScheduledDelay(string id)
    {
        using var connection = _storage.GetConnection();
        var state = connection.GetStateData(id);
        state.Name.ShouldBe("Scheduled");
        static DateTime ReadTimestamp(string value) => long.TryParse(value, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime
            : DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var enqueueAt = ReadTimestamp(state.Data["EnqueueAt"]);
        var scheduledAt = ReadTimestamp(state.Data["ScheduledAt"]);
        return enqueueAt - scheduledAt;
    }

    private async Task<string> WaitForRetry(string ds, int attempt)
    {
        string? id = null;
        await Until(async () =>
        {
            var state = await CacheState(ds);
            if (!state.TryGetProperty("RetryAttempt", out var value) || value.GetInt32() != attempt ||
                !state.TryGetProperty("ScheduledRetryJobId", out var job) || job.ValueKind != JsonValueKind.String)
                return false;
            id = job.GetString();
            using var connection = _storage.GetConnection();
            return id != null && connection.GetStateData(id)?.Name == "Scheduled";
        });
        return id!;
    }

    private async Task<JsonElement> CacheState(string ds)
    {
        using var document = JsonDocument.Parse(await Cache.GetAsync(MagicStrings.BuildPendingTrackerCacheSeed,
            CacheCategories.ForDataset(Org, ds)));
        return document.RootElement.Clone();
    }

    private async Task<JobIndex[]> Rows(string ds)
    {
        using var scope = _provider.CreateScope();
        return await new JobIndexQuery(scope.ServiceProvider.GetRequiredService<RegistryContext>())
            .GetByOrgDsAsync(Org, ds, take: int.MaxValue);
    }

    private async Task WaitForState(string id, string state)
    {
        await Until(() =>
        {
            using var connection = _storage.GetConnection();
            return Task.FromResult(connection.GetStateData(id)?.Name == state);
        });
        TestContext.Progress.WriteLine($"{DateTime.UtcNow:O} job {id}: {state}");
    }

    // Worker state is committed by Hangfire's MySQL transactions: no event to await
    private static Task Until(Func<Task<bool>> condition) =>
        Eventually.Until(condition, "expected worker/cache state", TimeSpan.FromSeconds(45));

    private static void MakeDue(IDDB ddb, string path)
    {
        var marker = Path.Combine(ddb.DerivedFolderPaths.Single(), ddb.GetEntry(path)!.Hash + ".pending");
        var lines = File.ReadAllLines(marker);
        lines[0] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - PendingMarkers.LibddbRetryBackoffSeconds - 1)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllLines(marker, lines);
    }

    private static string GridObj()
    {
        var text = new StringBuilder();
        const int n = 4;
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                text.Append(FormattableString.Invariant($"v {x} {y} {((x + y) % 3) * 0.25:R}\n"));
        for (var y = 0; y < n - 1; y++)
            for (var x = 0; x < n - 1; x++)
            {
                var a = y * n + x + 1;
                text.Append($"f {a} {a + 1} {a + n + 1}\nf {a} {a + n + 1} {a + n}\n");
            }
        return text.ToString();
    }

    private sealed class ProviderActivator(IServiceProvider provider) : JobActivator
    {
        public override JobActivatorScope BeginScope(JobActivatorContext context) => new ProviderScope(provider.CreateScope());
        private sealed class ProviderScope(IServiceScope scope) : JobActivatorScope
        {
            public override object Resolve(Type type) => scope.ServiceProvider.GetRequiredService(type);
            public override void DisposeScope() => scope.Dispose();
        }
    }

    private sealed class WorkerGate : IServerFilter
    {
        public string? BlockDataset;
        public int BlockedSnapshots;
        public int CoordinatorsStarted;
        public readonly ManualResetEventSlim FirstSnapshot = new();
        public readonly ManualResetEventSlim ReleaseSnapshots = new();
        public readonly ManualResetEventSlim ReleaseEntries = new(true);

        public void Reset()
        {
            BlockDataset = null;
            BlockedSnapshots = CoordinatorsStarted = 0;
            FirstSnapshot.Reset();
            ReleaseSnapshots.Reset();
            ReleaseEntries.Set();
        }

        public void BeforeSnapshot(string ds)
        {
            if (ds != BlockDataset) return;
            if (Interlocked.Increment(ref BlockedSnapshots) != 1) return;
            FirstSnapshot.Set();
            if (!ReleaseSnapshots.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Snapshot barrier not released.");
        }

        public void OnPerforming(PerformingContext context)
        {
            var job = DdbJobDescriptor.Of(context.BackgroundJob.Job);
            if (job?.Kind == DdbJobKind.BuildPending)
                Interlocked.Increment(ref CoordinatorsStarted);
            if (job?.Kind == DdbJobKind.Build &&
                !ReleaseEntries.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Entry barrier not released.");
        }

        public void OnPerformed(PerformedContext context) { }
    }
}
