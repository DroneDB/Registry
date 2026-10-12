#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Models;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Shared-storage coordination and retry control regressions. Cache reads return
/// the latest write; active job snapshots are taken inside the storage lock.
/// </summary>
[TestFixture]
public class PendingBuildCoordinationTests
{
    private const string Org = "pending-org";
    private const string Ds = "pending-ds";
    private readonly ConcurrentDictionary<string, byte[]> _cacheData = new();
    private readonly Dictionary<string, JobStatus> _statuses = new();
    private readonly List<TimeSpan> _delays = [];
    private Mock<ICacheManager> _cache = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IDdbManager> _manager = null!;
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private RegistryContext _context = null!;
    private InMemoryStorage _storage = null!;
    private BuildPendingService _service = null!;
    private int _enqueued;

    [SetUp]
    public void SetUp()
    {
        _cacheData.Clear();
        _statuses.Clear();
        _delays.Clear();
        _enqueued = 0;
        _storage = new InMemoryStorage();
        _context = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organization = new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow };
        organization.Datasets = [new Dataset
        {
            Slug = Ds, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
        }];
        _context.Organizations.Add(organization);
        _context.SaveChanges();
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/pending");
        _ddb.Setup(x => x.IsBuildPending()).Returns(true);
        _ddb.Setup(x => x.GetStamp()).Returns(new Stamp { Checksum = "unchanged", Entries = [], Meta = [] });
        _manager = new Mock<IDdbManager>();
        _manager.Setup(x => x.Get(Org, It.IsAny<Guid>())).Returns(_ddb.Object);
        _jobs = new Mock<IBackgroundJobsProcessor>();
        _jobs.Setup(x => x.GetJobStatus(It.IsAny<string>())).Returns<string>(id =>
            _statuses.GetValueOrDefault(id, JobStatus.Unknown));
        _jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Returns(() =>
            {
                var id = "sweep-" + ++_enqueued;
                _statuses[id] = JobStatus.Enqueued;
                return id;
            });
        _jobs.Setup(x => x.ScheduleIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>(), It.IsAny<TimeSpan>()))
            .Returns<Expression<Action>, IndexPayload, TimeSpan>((_, _, delay) =>
            {
                _delays.Add(delay);
                var id = "retry-" + _delays.Count;
                _statuses[id] = JobStatus.Scheduled;
                return id;
            });
        _cache = new Mock<ICacheManager>();
        _cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, object[]>((_, key, _) => Task.FromResult(_cacheData.GetValueOrDefault(key)!));
        _cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns<string, string, byte[], object[]>((_, key, value, _) =>
            {
                _cacheData[key] = value;
                return Task.CompletedTask;
            });
        _service = new BuildPendingService(_context, _manager.Object, _jobs.Object, _cache.Object,
            Mock.Of<ILogger<BuildPendingService>>(), _storage);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void Seed(int attempt = 0, string? jobId = null, bool pending = true) =>
        _cacheData[CacheCategories.ForDataset(Org, Ds)] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            HasPending = pending, LastCheckBinary = DateTime.UtcNow.ToBinary(), StampChecksum = "unchanged",
            RetryAttempt = attempt, ScheduledRetryJobId = jobId, RetryEpoch = "old-epoch"
        });

    private JsonElement State() => JsonDocument.Parse(_cacheData[CacheCategories.ForDataset(Org, Ds)]).RootElement;

    [TestCase(JobStatus.Unknown)]
    [TestCase(JobStatus.Failed)]
    [TestCase(JobStatus.Succeeded)]
    [TestCase(JobStatus.Deleted)]
    public async Task Sweep_UnchangedPendingRestartsOneEpochWhenTrackedCoordinatorIsGone(JobStatus status)
    {
        Seed(12, "old-job");
        _statuses["old-job"] = status;
        await _service.ProcessPendingBuilds();
        await _service.ProcessPendingBuilds();
        _enqueued.ShouldBe(1);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(0);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("sweep-1");
        State().GetProperty("RetryEpoch").GetString().ShouldNotBe("old-epoch");
    }

    [TestCase(JobStatus.Scheduled)]
    [TestCase(JobStatus.Enqueued)]
    [TestCase(JobStatus.Processing)]
    [TestCase(JobStatus.Awaiting)]
    public async Task Sweep_PreservesLiveRetryBudgetAndEpoch(JobStatus status)
    {
        Seed(8, "live");
        _statuses["live"] = status;
        await _service.ProcessPendingBuilds();
        _enqueued.ShouldBe(0);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(8);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("live");
        State().GetProperty("RetryEpoch").GetString().ShouldBe("old-epoch");
    }

    [Test]
    public async Task CompanionUploadAndFailureObservationPreserveLiveRetryControls()
    {
        Seed(8, "live");
        _statuses["live"] = JobStatus.Scheduled;
        await _service.SetDatasetHasPendingBuilds(Org, Ds, true);
        State().GetProperty("StampChecksum").ValueKind.ShouldBe(JsonValueKind.Null);
        await _service.RefreshPendingStatus(Org, Ds);
        await _service.ScheduleRetryIfPending(Org, Ds);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(8);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("live");
        State().GetProperty("RetryEpoch").GetString().ShouldBe("old-epoch");
        _delays.ShouldBeEmpty();
        _cache.Verify(x => x.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task NoPendingClearsControlsAndUnchangedDatasetThenSkipsNativeScan()
    {
        Seed(9, "live");
        _ddb.Setup(x => x.IsBuildPending()).Returns(false);
        await _service.ProcessPendingBuilds();
        await _service.ProcessPendingBuilds();
        State().GetProperty("HasPending").GetBoolean().ShouldBeFalse();
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(0);
        State().GetProperty("ScheduledRetryJobId").ValueKind.ShouldBe(JsonValueKind.Null);
        State().GetProperty("RetryEpoch").ValueKind.ShouldBe(JsonValueKind.Null);
        _ddb.Verify(x => x.IsBuildPending(), Times.Once);
        _enqueued.ShouldBe(0);
    }

    [TestCase("{invalid")]
    [TestCase("null")]
    [TestCase("")]
    public async Task MissingOrMalformedCacheStartsBoundedRecovery(string json)
    {
        _cacheData[CacheCategories.ForDataset(Org, Ds)] = Encoding.UTF8.GetBytes(json);
        await _service.ProcessPendingBuilds();
        await _service.ProcessPendingBuilds();
        _enqueued.ShouldBe(1);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task StampFailureCannotPermanentlySuppressPendingWork()
    {
        Seed(pending: false);
        _ddb.Setup(x => x.GetStamp()).Throws(new DdbException("stamp unavailable"));
        await _service.ProcessPendingBuilds();
        _enqueued.ShouldBe(1);
    }

    [Test]
    public async Task SweepEnqueueFailureDoesNotResetEpochOrConsumeBudget()
    {
        Seed(12, "old");
        _jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Throws(new InvalidOperationException("index storage unavailable"));
        await _service.ProcessPendingBuilds();
        _service.GetStatus().Errors.ShouldBe(1);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(12);
        State().GetProperty("RetryEpoch").GetString().ShouldBe("old-epoch");
    }

    [Test]
    public async Task DelayedScheduleFailureDoesNotConsumeBudget()
    {
        Seed(4);
        _jobs.Setup(x => x.ScheduleIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>(), It.IsAny<TimeSpan>()))
            .Throws(new InvalidOperationException("queue unavailable"));
        await _service.ScheduleRetryIfPending(Org, Ds);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
    }

    [Test]
    public async Task RetryPolicyStopsAtTwelveAndRestartsOnlyOnSweep()
    {
        Seed();
        for (var i = 0; i < 13; i++)
        {
            await _service.ScheduleRetryIfPending(Org, Ds);
            if (i < 12) _statuses["retry-" + (i + 1)] = JobStatus.Succeeded;
        }
        _delays.Select(d => d.TotalMinutes).ShouldBe(new double[] { 1, 2, 5, 10, 20, 40, 60, 60, 60, 60, 60, 60 });
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(12);
        await _service.ProcessPendingBuilds();
        _enqueued.ShouldBe(1);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(0);
    }

    [Test]
    public async Task TerminalCoordinatorSchedulesNextRetryEvenBeforeStateTransactionCommits()
    {
        Seed(3, "just-finished");
        _statuses["just-finished"] = JobStatus.Processing;
        await _service.ScheduleRetryIfPending(Org, Ds, "just-finished", null, null);
        _delays.ShouldBe([TimeSpan.FromMinutes(10)]);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("retry-1");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void FailedEntryBothFilterOrdersPreserveNewlyScheduledRetryAndError(bool failureFirst)
    {
        Seed(3);
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([new PendingBuildInfo { Path = "model.obj", Hash = "model-hash" }]);
        var writer = new Mock<IJobIndexWriter>();
        using var provider = new ServiceCollection().AddSingleton(_service).AddSingleton(writer.Object).BuildServiceProvider();
        var failed = new BuildJobFailureFilter(provider, Mock.Of<ILogger<BuildJobFailureFilter>>());
        var retry = new BuildRetrySchedulerFilter(provider, Mock.Of<ILogger<BuildRetrySchedulerFilter>>());
        var context = FilterContext("model.obj", "model-hash", new FailedState(new DdbException("timeout")));
        if (failureFirst)
        {
            failed.OnStateApplied(context, Mock.Of<IWriteOnlyTransaction>());
            retry.OnStateApplied(context, Mock.Of<IWriteOnlyTransaction>());
        }
        else
        {
            retry.OnStateApplied(context, Mock.Of<IWriteOnlyTransaction>());
            failed.OnStateApplied(context, Mock.Of<IWriteOnlyTransaction>());
        }
        _delays.ShouldBe([TimeSpan.FromMinutes(10)]);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("retry-1");
        writer.Verify(x => x.UpdateErrorAsync("entry-job", nameof(DdbException), default), Times.Once);
    }

    [TestCase("other.obj", "model-hash")]
    [TestCase("model.obj", "other-hash")]
    [TestCase("model.obj", "")]
    public void FailedEntryRequiresItsOwnMatchingNativePendingMarker(string pendingPath, string pendingHash)
    {
        Seed();
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([new PendingBuildInfo { Path = pendingPath, Hash = pendingHash }]);
        using var provider = new ServiceCollection().AddSingleton(_service).BuildServiceProvider();
        new BuildRetrySchedulerFilter(provider, Mock.Of<ILogger<BuildRetrySchedulerFilter>>())
            .OnStateApplied(FilterContext("model.obj", "model-hash", new FailedState(new DdbException("invalid input"))),
                Mock.Of<IWriteOnlyTransaction>());
        _delays.ShouldBeEmpty();
    }

    private ApplyStateContext FilterContext(string path, string hash, IState state)
    {
        var connection = new Mock<JobStorageConnection>();
        connection.Setup(x => x.GetJobParameter("entry-job", JobParamKeys.OrgSlug)).Returns(Org);
        connection.Setup(x => x.GetJobParameter("entry-job", JobParamKeys.DsSlug)).Returns(Ds);
        connection.Setup(x => x.GetJobParameter("entry-job", JobParamKeys.Hash)).Returns(hash);
        var job = Job.FromExpression(() => HangfireUtils.BuildWrapper(_ddb.Object, path, false, null));
        var background = new BackgroundJob("entry-job", job, DateTime.UtcNow);
        return new ApplyStateContext(_storage, connection.Object, Mock.Of<IWriteOnlyTransaction>(), background, state, "Processing");
    }

    [Test]
    public async Task OverlappingCoordinatorsTakeFreshSnapshotsInsideLockAndEnqueueOnce()
    {
        using var firstReading = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var snapshots = 0;
        var rows = new List<JobIndex>();
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/coordination");
        ddb.Setup(x => x.GetPendingBuildInfo()).Returns(() =>
        {
            if (Interlocked.Increment(ref snapshots) == 1)
            {
                firstReading.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test barrier not released");
            }
            return [new PendingBuildInfo { Path = "model.obj", Hash = "hash", LastAttempt = 0 }];
        });
        var index = new Mock<IJobIndexQuery>();
        index.Setup(x => x.GetByOrgDsAsync(Org, Ds, 0, int.MaxValue, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(rows.ToArray()));
        var count = 0;
        var jobs = new Mock<IBackgroundJobsProcessor>();
        jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Returns<Expression<Action>, IndexPayload>((_, meta) =>
            {
                count++;
                var id = new BackgroundJobClient(_storage).Create(Job.FromExpression(() => HangfireUtils.DummyJob(null)),
                    new EnqueuedState());
                rows.Add(new JobIndex { JobId = id, OrgSlug = Org, DsSlug = Ds, Path = meta.Path,
                    ToolId = "build", CurrentState = "Enqueued" });
                return id;
            });
        void Run() => HangfireUtils.BuildPendingWrapper(ddb.Object, null, jobs.Object, index.Object, Org, Ds,
            () => 1_800_000_000, _storage);
        var first = Task.Run(Run);
        firstReading.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        var second = Task.Run(() => { secondStarted.Set(); Run(); });
        secondStarted.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        try
        {
            await Task.Delay(100);
            Volatile.Read(ref snapshots).ShouldBe(1);
        }
        finally { releaseFirst.Set(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        count.ShouldBe(1);
        snapshots.ShouldBe(2);
    }

    [Test]
    public void DifferentDatasetsUseIndependentLocks()
    {
        using var sameDataset = DatasetBuildCoordinator.Acquire(Org, Ds, _storage);
        using var differentDataset = DatasetBuildCoordinator.Acquire(Org, "another-dataset", _storage);
        DatasetBuildCoordinator.ResourceKey(Org, Ds).ShouldNotBe(DatasetBuildCoordinator.ResourceKey(Org, "another-dataset"));
        DatasetBuildCoordinator.ResourceKey("a/b", "c").ShouldNotBe(DatasetBuildCoordinator.ResourceKey("a", "b/c"));
        DatasetBuildCoordinator.ResourceKey(Org, Ds).Length.ShouldBeLessThan(65);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OnlyDistributedLockTimeoutBecomesRetryableCoordinatorContention(bool mysqlProvider)
    {
        var connection = new Mock<IStorageConnection>();
        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(mysqlProvider
                ? new Hangfire.MySql.MySqlDistributedLockException("cannot acquire lock")
                : new DistributedLockTimeoutException("busy"));
        Should.Throw<PendingBuildCoordinatorContentionException>(() =>
            DatasetBuildCoordinator.Acquire(Org, Ds, _storage, connection.Object));
        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new InvalidOperationException("storage unavailable"));
        Should.Throw<InvalidOperationException>(() =>
            DatasetBuildCoordinator.Acquire(Org, Ds, _storage, connection.Object));
        var policy = typeof(HangfireUtils).GetMethod(nameof(HangfireUtils.BuildPendingWrapper))!
            .GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<AutomaticRetryAttribute>().Single();
        policy.OnlyOn.ShouldContain(typeof(PendingBuildCoordinatorContentionException));
        policy.Attempts.ShouldBe(5);
    }
    [Test]
    public void CoordinatorLockTimeoutDoesNotTakePendingOrActiveSnapshots()
    {
        var connection = new Mock<IStorageConnection>();
        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new DistributedLockTimeoutException("busy"));
        var storage = new Mock<JobStorage>();
        storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        var index = new Mock<IJobIndexQuery>();
        Should.Throw<PendingBuildCoordinatorContentionException>(() =>
            HangfireUtils.BuildPendingWrapper(_ddb.Object, null, _jobs.Object, index.Object, Org, Ds,
                () => 1_800_000_000, storage.Object));
        _ddb.Verify(x => x.GetPendingBuildInfo(), Times.Never);
        index.Verify(x => x.GetByOrgDsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobs.Verify(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()), Times.Never);
    }

    [Test]
    public void EntryEnqueueFailureRemainsVisibleAndDoesNotRunNativeFallback()
    {
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([new PendingBuildInfo { Path = "model.obj", Hash = "h", LastAttempt = 0 }]);
        var index = new Mock<IJobIndexQuery>();
        index.Setup(x => x.GetByOrgDsAsync(Org, Ds, 0, int.MaxValue, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _jobs.Setup(x => x.EnqueueIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>()))
            .Throws(new InvalidOperationException("index storage unavailable"));
        Should.Throw<InvalidOperationException>(() => HangfireUtils.BuildPendingWrapper(_ddb.Object, null,
            _jobs.Object, index.Object, Org, Ds, () => 1_800_000_000, _storage));
        _ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public void MissingStoredCoordinatorStatusIsUnknownInsteadOfThrowing()
    {
        var connection = new Mock<IStorageConnection>();
        var storage = new Mock<JobStorage>();
        storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        using var scope = new JobStorageScope(storage.Object);
        new BackgroundJobsProcessor(Mock.Of<IBackgroundJobClient>(), Mock.Of<IIndexedJobEnqueuer>())
            .GetJobStatus("missing").ShouldBe(JobStatus.Unknown);
    }

    [Test]
    public async Task SeparateHostCachesShareOneCoordinatorAndOneRetryBudget()
    {
        Seed(3);
        var secondData = new ConcurrentDictionary<string, byte[]>();
        var secondCache = new Mock<ICacheManager>();
        secondCache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, object[]>((_, key, _) => Task.FromResult(secondData.GetValueOrDefault(key)!));
        secondCache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns<string, string, byte[], object[]>((_, key, value, _) =>
            {
                secondData[key] = value;
                return Task.CompletedTask;
            });
        ServiceProvider Host(ICacheManager cache) => new ServiceCollection()
            .AddSingleton(_context).AddSingleton(_manager.Object).AddSingleton(_jobs.Object).AddSingleton(cache)
            .AddSingleton<JobStorage>(_storage).AddSingleton(Mock.Of<ILogger<BuildPendingService>>())
            .AddScoped<BuildPendingService>().BuildServiceProvider();
        using var firstHost = Host(_cache.Object);
        using var secondHost = Host(secondCache.Object);
        using var firstScope = firstHost.CreateScope();
        using var secondScope = secondHost.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<BuildPendingService>();
        var second = secondScope.ServiceProvider.GetRequiredService<BuildPendingService>();
        await first.ScheduleRetryIfPending(Org, Ds);
        await second.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBe([TimeSpan.FromMinutes(10)]);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
        JsonDocument.Parse(secondData[CacheCategories.ForDataset(Org, Ds)]).RootElement
            .GetProperty("RetryAttempt").GetInt32().ShouldBe(4);

        _statuses["retry-1"] = JobStatus.Succeeded;
        await second.ScheduleRetryIfPending(Org, Ds);
        await first.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBe([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)]);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(5);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("retry-2");
    }

    [TestCase(8, "live")]
    [TestCase(12, null)]
    public async Task ActualCategoryInvalidationCannotEraseSharedRetryControls(int attempt, string? jobId)
    {
        Seed(attempt, jobId);
        if (jobId != null) _statuses[jobId] = JobStatus.Scheduled;
        // Import the legacy fields once, then run the normal generic invalidator.
        await _service.SetDatasetHasPendingBuilds(Org, Ds, true);
        _cache.Setup(x => x.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((seed, category) =>
            {
                if (seed == MagicStrings.BuildPendingTrackerCacheSeed) _cacheData.TryRemove(category, out _);
                return Task.CompletedTask;
            });
        await new DatasetCacheInvalidator(Mock.Of<ILogger<DatasetCacheInvalidator>>(), _cache.Object,
            Mock.Of<ICacheKeyScanner>()).InvalidateAllDatasetCachesAsync(Org, Ds);
        _cacheData.ShouldBeEmpty();
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBeEmpty();
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(attempt);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe(jobId);
    }

    [Test]
    public async Task LegacyCacheControlsCannotOverwriteAlreadyImportedAuthority()
    {
        Seed(8, "live");
        _statuses["live"] = JobStatus.Scheduled;
        await _service.SetDatasetHasPendingBuilds(Org, Ds, true);
        Seed(0);
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBeEmpty();
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(8);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("live");
    }

    [TestCase("Indexing")]
    [TestCase("Failed")]
    [TestCase("Missing")]
    public void StaleCreatedRowCannotBlockNextPendingAttempt(string actualState)
    {
        var storage = new Mock<JobStorage>();
        var connection = new Mock<IStorageConnection>();
        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(Mock.Of<IDisposable>());
        connection.Setup(x => x.GetStateData("stale-job")).Returns(actualState == "Missing" ? null :
            new StateData { Name = actualState, Data = new Dictionary<string, string>() });
        storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        _ddb.Setup(x => x.GetPendingBuildInfo()).Returns([new PendingBuildInfo { Path = "model.obj", Hash = "h", LastAttempt = 0 }]);
        var index = new Mock<IJobIndexQuery>();
        index.Setup(x => x.GetByOrgDsAsync(Org, Ds, 0, int.MaxValue, It.IsAny<CancellationToken>())).ReturnsAsync([
            new JobIndex { JobId = "stale-job", OrgSlug = Org, DsSlug = Ds, Path = "model.obj", ToolId = "build", CurrentState = "Created" }
        ]);
        HangfireUtils.BuildPendingWrapper(_ddb.Object, null, _jobs.Object, index.Object, Org, Ds,
            () => 1_800_000_000, storage.Object);
        _enqueued.ShouldBe(1);
    }

    [Test]
    public async Task CacheReadFaultUsesSharedBudgetAndPreservesNewLiveCoordinator()
    {
        Seed(3);
        await _service.SetDatasetHasPendingBuilds(Org, Ds, true);
        _cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("observation cache unavailable"));
        await _service.ScheduleRetryIfPending(Org, Ds);
        await _service.ProcessPendingBuilds();
        _delays.ShouldBe([TimeSpan.FromMinutes(10)]);
        _enqueued.ShouldBe(0);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("retry-1");
        using var connection = _storage.GetConnection();
        using var shared = JsonDocument.Parse(connection.GetAllEntriesFromHash(DatasetBuildCoordinator.RetryStateKey(Org, Ds))["Controls"]);
        shared.RootElement.GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
    }

    [Test]
    public async Task CacheReadFaultStillAllowsOneSafetyNetRecoveryEpoch()
    {
        Seed(12);
        await _service.SetDatasetHasPendingBuilds(Org, Ds, true);
        _cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("observation cache unavailable"));
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBeEmpty();
        await _service.ProcessPendingBuilds();
        await _service.ProcessPendingBuilds();
        _enqueued.ShouldBe(1);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(0);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("sweep-1");
    }

    [Test]
    public async Task CacheWriteFaultWithSeparateStaleHostCannotDuplicateCoordinatorOrResetBudget()
    {
        Seed(3);
        var stale = _cacheData[CacheCategories.ForDataset(Org, Ds)].ToArray();
        _cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .ThrowsAsync(new InvalidOperationException("observation cache write unavailable"));
        var secondCache = new Mock<ICacheManager>();
        secondCache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(stale);
        secondCache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .ThrowsAsync(new InvalidOperationException("second host observation cache write unavailable"));
        var second = new BuildPendingService(_context, _manager.Object, _jobs.Object, secondCache.Object,
            Mock.Of<ILogger<BuildPendingService>>(), _storage);

        await _service.ScheduleRetryIfPending(Org, Ds);
        await second.ScheduleRetryIfPending(Org, Ds);
        await _service.ProcessPendingBuilds();
        _delays.ShouldBe([TimeSpan.FromMinutes(10)]);
        _enqueued.ShouldBe(0);
        // Both cache snapshots remain stale; deduplication and accounting must
        // come from shared storage despite the failed observation publication.
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(3);
        _service.GetStatus().Errors.ShouldBe(1);
        using var connection = _storage.GetConnection();
        using (var shared = JsonDocument.Parse(connection.GetAllEntriesFromHash(
                   DatasetBuildCoordinator.RetryStateKey(Org, Ds))["Controls"]))
        {
            shared.RootElement.GetProperty("RetryAttempt").GetInt32().ShouldBe(4);
            shared.RootElement.GetProperty("ScheduledRetryJobId").GetString().ShouldBe("retry-1");
        }

        _statuses["retry-1"] = JobStatus.Succeeded;
        await second.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBe([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20)]);
        using var advanced = JsonDocument.Parse(connection.GetAllEntriesFromHash(
            DatasetBuildCoordinator.RetryStateKey(Org, Ds))["Controls"]);
        advanced.RootElement.GetProperty("RetryAttempt").GetInt32().ShouldBe(5);
        advanced.RootElement.GetProperty("ScheduledRetryJobId").GetString().ShouldBe("retry-2");
    }

    [Test]
    public async Task EmptyFirstHostLeavesImportOpenForPopulatedLegacyHost()
    {
        await _service.SetDatasetHasPendingBuilds(Org, Ds, true);
        State().GetProperty("RetryControlsVersion").GetInt32().ShouldBe(1);
        Seed(8, "legacy-live");
        _statuses["legacy-live"] = JobStatus.Scheduled;
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBeEmpty();
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(8);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("legacy-live");
    }

    [Test]
    public async Task PopulatedLegacyHostConservativelyMergesWithoutReplacingNewSharedLiveCoordinator()
    {
        var client = new BackgroundJobClient(_storage);
        _jobs.Setup(x => x.ScheduleIndexed(It.IsAny<Expression<Action>>(), It.IsAny<IndexPayload>(), It.IsAny<TimeSpan>()))
            .Returns<Expression<Action>, IndexPayload, TimeSpan>((_, _, delay) =>
            {
                _delays.Add(delay);
                var id = client.Create(Job.FromExpression(() => HangfireUtils.DummyJob(null)), new ScheduledState(delay));
                _statuses[id] = JobStatus.Scheduled;
                return id;
            });
        await _service.ScheduleRetryIfPending(Org, Ds);
        var sharedLive = State().GetProperty("ScheduledRetryJobId").GetString();
        State().GetProperty("RetryControlsVersion").GetInt32().ShouldBe(1);
        var legacyLive = client.Create(Job.FromExpression(() => HangfireUtils.DummyJob(null)),
            new ScheduledState(TimeSpan.FromHours(1)));
        _statuses[legacyLive] = JobStatus.Scheduled;
        Seed(8, legacyLive);
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBe([TimeSpan.FromMinutes(1)]);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(8);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe(sharedLive);
    }

    [Test]
    public async Task CategoryInvalidationImportsAvailableLegacyBudgetBeforeFirstServiceCheck()
    {
        Seed(8, "legacy-live");
        _statuses["legacy-live"] = JobStatus.Scheduled;
        _cache.Setup(x => x.IsRegistered(MagicStrings.BuildPendingTrackerCacheSeed)).Returns(true);
        _cache.Setup(x => x.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((seed, category) =>
            {
                if (seed == MagicStrings.BuildPendingTrackerCacheSeed) _cacheData.TryRemove(category, out _);
                return Task.CompletedTask;
            });
        await new DatasetCacheInvalidator(Mock.Of<ILogger<DatasetCacheInvalidator>>(), _cache.Object,
            Mock.Of<ICacheKeyScanner>(), _storage).InvalidateAllDatasetCachesAsync(Org, Ds);
        _cacheData.ShouldBeEmpty();
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBeEmpty();
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(8);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("legacy-live");
    }

    [Test]
    public async Task CategoryInvalidationKeepsOnlyLegacyTrackerWhenSharedPreservationFails()
    {
        Seed(8, "legacy-live");
        _statuses["legacy-live"] = JobStatus.Scheduled;
        _cache.Setup(x => x.IsRegistered(MagicStrings.BuildPendingTrackerCacheSeed)).Returns(true);
        _cache.Setup(x => x.RemoveByCategoryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((seed, category) =>
            {
                if (seed == MagicStrings.BuildPendingTrackerCacheSeed) _cacheData.TryRemove(category, out _);
                return Task.CompletedTask;
            });
        var connection = new Mock<IStorageConnection>();
        connection.Setup(x => x.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(Mock.Of<IDisposable>());
        connection.Setup(x => x.SetRangeInHash(It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>()))
            .Throws(new InvalidOperationException("shared retry controls unavailable"));
        var storage = new Mock<JobStorage>();
        storage.Setup(x => x.GetConnection()).Returns(connection.Object);
        await new DatasetCacheInvalidator(Mock.Of<ILogger<DatasetCacheInvalidator>>(), _cache.Object,
            Mock.Of<ICacheKeyScanner>(), storage.Object).InvalidateAllDatasetCachesAsync(Org, Ds);

        _cache.Verify(x => x.RemoveByCategoryAsync(MagicStrings.BuildPendingTrackerCacheSeed,
            CacheCategories.ForDataset(Org, Ds)), Times.Never);
        _cache.Verify(x => x.RemoveByCategoryAsync(MagicStrings.TileCacheSeed,
            CacheCategories.ForDataset(Org, Ds)), Times.Once);
        _cache.Verify(x => x.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed,
            CacheCategories.ForDataset(Org, Ds)), Times.Once);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(8);
        // When storage returns, importing the preserved tracker must retain the
        // existing retry chain rather than schedule an immediate fresh budget.
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBeEmpty();
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("legacy-live");
    }

    [Test]
    public async Task AuthoritativeNoPendingEndsImportSoOldHostCannotResurrectCompletedControls()
    {
        _ddb.Setup(x => x.IsBuildPending()).Returns(false);
        await _service.ProcessPendingBuilds();
        Seed(8, "legacy-live");
        _statuses["legacy-live"] = JobStatus.Scheduled;
        _ddb.Setup(x => x.IsBuildPending()).Returns(true);
        await _service.ScheduleRetryIfPending(Org, Ds);
        _delays.ShouldBe([TimeSpan.FromMinutes(1)]);
        State().GetProperty("RetryAttempt").GetInt32().ShouldBe(1);
        State().GetProperty("ScheduledRetryJobId").GetString().ShouldBe("retry-1");
    }

}
