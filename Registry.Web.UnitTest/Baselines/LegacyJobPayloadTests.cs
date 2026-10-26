#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Server;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Attributes;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Every job shape that production storage (Registry v2.7.1, MySQL) can hold when the
/// release starts still deserializes and runs. The payloads are the exact Hangfire InvocationData
/// JSON captured from that release with the production serializer settings
/// (CompatibilityLevel 1.8, simple assembly names, recommended settings); only the dataset root is
/// moved to a temporary folder. Each one is deserialized as Hangfire does it, in the compact payload
/// and in the split form MySqlStorage stores (InvocationData without arguments + Arguments column),
/// and performed through Hangfire's own BackgroundJobPerformer with the ASP.NET Core activator, so
/// the job reaches the new implementation (DdbJobs, FileJobs, ...) with the right dataset and
/// arguments, and keeps the retry policy it was stored with.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class LegacyJobPayloadTests : IDisposable
{
    // The dataset root and file paths of the captured payloads, replaced per test
    private const string CapturedRoot = LegacyJobPayloads.CapturedRoot;
    private const string Org = "org1";
    private static readonly Guid Ref = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    private const string Ctx = "Hangfire.Server.PerformContext, Hangfire.Core";
    private static IReadOnlyDictionary<string, string> Captured => LegacyJobPayloads.Captured;

    /// <summary>The recurring jobs of v2.7.1: (type, method), stored as X(PerformContext).</summary>
    private static readonly (Type Type, string Method)[] Recurring =
    [
        (typeof(JobIndexSyncService), nameof(JobIndexSyncService.SyncJobIndexStates)),
        (typeof(IndexedJobPublicationRecoveryService), nameof(IndexedJobPublicationRecoveryService.RecoverPublications)),
        (typeof(BuildPendingService), nameof(BuildPendingService.ProcessPendingBuilds)),
        (typeof(OrphanedDatasetCleanupService), nameof(OrphanedDatasetCleanupService.CleanupOrphanedFoldersAsync)),
        (typeof(JobIndexCleanupService), nameof(JobIndexCleanupService.CleanupOldJobIndicesAsync)),
        (typeof(RecurringDatasetCleanupService), nameof(RecurringDatasetCleanupService.CleanupAllDatasetsAsync)),
        (typeof(ArtifactCompletenessCheckerService), nameof(ArtifactCompletenessCheckerService.CheckAndQueueAsync)),
        (typeof(IndexReconciliationService), nameof(IndexReconciliationService.ReconcileAllDatasetsAsync))
    ];

    private string _root = null!;
    private string _folder = null!;
    private Mock<IDdbWrapper> _wrapper = null!;
    private InMemoryStorage _storage = null!;
    private ServiceProvider _provider = null!;
    private Registry.Web.Data.RegistryContext _context = null!;

    [OneTimeSetUp]
    public void UseProductionSerializerSettings() =>
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "legacyjobpayload-d4-" + Guid.NewGuid().ToString("N"));
        _folder = Path.Combine(_root, Org, Ref.ToString());
        Directory.CreateDirectory(Path.Combine(_folder, IDDB.DatabaseFolderName));
        _wrapper = new Mock<IDdbWrapper>();
        _wrapper.Setup(x => x.Cleanup(It.IsAny<string>())).Returns(new DdbCleanupResult());
        _storage = new InMemoryStorage();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<JobStorage>(_storage);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton(_wrapper.Object);
        services.AddSingleton<IOptions<StorageOptions>>(Microsoft.Extensions.Options.Options.Create(new StorageOptions { DatasetsPath = _root }));
        services.AddScoped<IDdbManager, DdbManager>();
        services.AddSingleton(Mock.Of<IBackgroundJobsProcessor>());
        services.AddSingleton(Mock.Of<IJobIndexQuery>());
        _context = TestDb.CreateContext<Registry.Web.Data.RegistryContext>();
        services.AddSingleton(_context);
        services.AddScoped<DatasetCleanupService>();
        services.AddScoped<PendingBuildEnqueuer>();
        services.AddSingleton(TestJobs.Budget());
        services.AddScoped<DdbJobs>();
        services.AddScoped<FileJobs>();
        services.AddScoped<HealthCheckJob>();
        services.AddScoped<ExpiredRecurringJobsSweep>();
        services.AddScoped<HangfireUtils>();
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _context.Dispose();
        _storage.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Payload(string name) => Captured[name].Replace(CapturedRoot, _root.Replace('\\', '/'));

    // As Hangfire reads a stored job: the compact payload, or MySqlStorage's split columns
    private static Job Deserialize(string payload, bool split)
    {
        if (!split)
            return InvocationData.DeserializePayload(payload).DeserializeJob();

        var json = JObject.Parse(payload);
        var arguments = json["a"]!.ToString(Newtonsoft.Json.Formatting.None);
        json.Remove("a");
        var data = InvocationData.DeserializePayload(json.ToString(Newtonsoft.Json.Formatting.None));
        return new InvocationData(data.Type, data.Method, data.ParameterTypes, arguments).DeserializeJob();
    }

    private void Perform(Job job)
    {
        var activator = new AspNetCoreJobActivator(_provider.GetRequiredService<IServiceScopeFactory>());
        var performer = new BackgroundJobPerformer(JobFilterProviders.Providers, activator, TaskScheduler.Default);
        using var connection = _storage.GetConnection();
        var context = new PerformContext(_storage, connection, new BackgroundJob("d4-job", job, DateTime.UtcNow),
            new JobCancellationToken(false));
        performer.Perform(context);
    }

    private static AutomaticRetryAttribute RetryPolicy(Job job) =>
        JobFilterProviders.Providers.GetFilters(job).Select(f => f.Instance).OfType<AutomaticRetryAttribute>().Single();

    [TestCase(false)]
    [TestCase(true)]
    public void BuildWrapper_ReachesDdbJobsBuildOnTheStoredDatasetFolder(bool split)
    {
        Perform(Deserialize(Payload("BuildWrapper"), split));
        Perform(Deserialize(Payload("BuildWrapperNullPath"), split));

        _wrapper.Verify(x => x.Build(_folder, "a/b.tif", null, false, false), Times.Once);
        _wrapper.Verify(x => x.Build(_folder, null, null, true, false), Times.Once);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BuildPendingWrapper_ReachesDdbJobsBuildPending(bool split)
    {
        // The test job has no org/ds parameters: the pending job runs libddb's pending build
        // inline (the native fallback it always had without them)
        Perform(Deserialize(Payload("BuildPendingWrapper"), split));

        _wrapper.Verify(x => x.Build(_folder, null, null, false, true), Times.Once);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CleanupWrapper_ReachesDdbJobsCleanup(bool split)
    {
        Perform(Deserialize(Payload("CleanupWrapper"), split));

        _wrapper.Verify(x => x.Cleanup(_folder), Times.Once);
    }

    [Test]
    public void GenerateThumbnailWrapper_ReachesDdbJobsGenerateThumbnail()
    {
        var dest = Path.Combine(_root, "thumb.jpg");
        _wrapper.Setup(x => x.GenerateThumbnail(It.IsAny<string>(), 256)).Returns([1, 2, 3]);

        Perform(Deserialize(Payload("GenerateThumbnailWrapper").Replace("/tmp/t.jpg", dest.Replace('\\', '/')), false));

        _wrapper.Verify(x => x.GenerateThumbnail(Path.Combine(_folder, "a.jpg"), 256), Times.Once);
        File.ReadAllBytes(dest).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Test]
    public void MaskBordersWrapper_ReachesDdbJobsMaskBorders()
    {
        Perform(Deserialize(Payload("MaskBordersWrapper"), false));

        _wrapper.Verify(x => x.MaskBorders(Path.Combine(_folder, "a.tif"), Path.Combine(_folder, "a_masked.tif"),
            15, true), Times.Once);
        _wrapper.Verify(x => x.Build(_folder, "a_masked.tif", null, false, false), Times.Once);
    }

    [TestCase("SafeDelete", "/tmp/x")]
    [TestCase("DeleteWorkDirJob", "/tmp/w")]
    public void StoredDeletes_ReachFileJobsDelete(string name, string capturedPath)
    {
        var target = Path.Combine(_root, "work-" + name);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "artifact.zip"), "x");

        // The heavy-task wrapper's own shim needs its full container; this one only resolves it
        if (name == "DeleteWorkDirJob")
        {
            var job = Deserialize(Payload(name).Replace(capturedPath, target.Replace('\\', '/')), false);
            job.Type.ShouldBe(typeof(HeavyTaskJobWrapper));
            job.Method.Name.ShouldBe(nameof(HeavyTaskJobWrapper.DeleteWorkDirJob));
            job.Args[0].ShouldBe(target.Replace('\\', '/'));
            RetryPolicy(job).Attempts.ShouldBe(1);
            return;
        }

        Perform(Deserialize(Payload(name).Replace(capturedPath, target.Replace('\\', '/')), false));

        Directory.Exists(target).ShouldBeFalse();
    }

    [TestCase("CleanupExpiredJobs")]
    [TestCase("DummyJob")]
    public void StoredMaintenanceJobs_Run(string name) =>
        Should.NotThrow(() => Perform(Deserialize(Payload(name), false)));

    [Test]
    public void LegacyShims_KeepTheRetryPolicyTheyWereStoredWith()
    {
        Job Legacy(string name) => Deserialize(Payload(name), false);

        // Build: contention only; a retryable build failure fails the job
        var build = RetryPolicy(Legacy("BuildWrapper"));
        build.Attempts.ShouldBe(5);
        build.DelaysInSeconds.ShouldBe([15, 60, 180, 600, 1800]);
        build.OnlyOn.ShouldBe([typeof(DdbBusyException), typeof(DdbBuildInProgressException)], ignoreOrder: true);

        foreach (var name in new[] { "CleanupWrapper", "GenerateThumbnailWrapper", "MaskBordersWrapper" })
        {
            var policy = RetryPolicy(Legacy(name));
            policy.Attempts.ShouldBe(5);
            policy.OnlyOn.ShouldContain(typeof(DdbRetryableException));
            policy.OnlyOn.ShouldContain(typeof(DdbBusyException));
            policy.OnlyOn.ShouldContain(typeof(DdbBuildInProgressException));
        }

        RetryPolicy(Legacy("BuildPendingWrapper")).OnlyOn.ShouldContain(typeof(PendingBuildCoordinatorContentionException));

        foreach (var name in new[] { "SafeDelete", "CleanupExpiredJobs", "DummyJob" })
        {
            var policy = RetryPolicy(Legacy(name));
            policy.Attempts.ShouldBe(1);
            policy.OnAttemptsExceeded.ShouldBe(AttemptsExceededAction.Fail);
        }

        JobFilterProviders.Providers.GetFilters(Legacy("DummyJob")).Select(f => f.Instance)
            .OfType<JobExpirationAttribute>().Single().ExpirationTimeoutInMinutes.ShouldBe(5);
    }

    [Test]
    public void NewShapes_HaveTheSamePolicies()
    {
        var build = RetryPolicy(Job.FromExpression<DdbJobs>(j => j.Build(Org, Ref, "a", false, null)));
        build.OnlyOn.ShouldBe([typeof(DdbBusyException), typeof(DdbBuildInProgressException)], ignoreOrder: true);
        RetryPolicy(Job.FromExpression<DdbJobs>(j => j.Cleanup(Org, Ref, null))).OnlyOn
            .ShouldContain(typeof(DdbRetryableException));
        RetryPolicy(Job.FromExpression<FileJobs>(j => j.Delete("x", null))).Attempts.ShouldBe(1);
        var cleanup = RetryPolicy(Job.FromExpression<DatasetCleanupService>(s => s.CleanupDeletedAsync(Org, Ref, null)));
        var storedCleanup = RetryPolicy(Deserialize(Payload("CleanupDeletedDataset"), true));
        cleanup.Attempts.ShouldBe(storedCleanup.Attempts);
        cleanup.DelaysInSeconds.ShouldBe(storedCleanup.DelaysInSeconds);
    }

    [Test]
    public void HeavyTaskRun_StillResolvesToTheSameMethod()
    {
        var job = Deserialize(Payload("HeavyRun"), true);

        job.Type.ShouldBe(typeof(HeavyTaskJobWrapper));
        job.Method.ShouldBe(typeof(HeavyTaskJobWrapper).GetMethod(nameof(HeavyTaskJobWrapper.Run)));
        job.Args.Take(3).ShouldBe(["build", "1", "{}"]);
    }

    [Test]
    public void CleanupDeletedDataset_StillResolves()
    {
        var job = Deserialize(Payload("CleanupDeletedDataset"), true);

        job.Method.Name.ShouldBe(nameof(DatasetCleanupService.CleanupDeletedDatasetAsync));
        job.Args[2].ShouldBe(Ref);
    }

    /// <summary>
    /// A cleanup queued before a deleted dataset's rows moved to a tombstone names the slug: the stored job, activated
    /// through DI, still runs as it was queued (it cleans by slug), and is not mistaken for the new cleanup.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void CleanupDeletedDataset_StillCleansBySlugAsQueued(bool split)
    {
        _context.JobIndices.AddRange(Row("stored", Org, "ds1"), Row("other-dataset", Org, "ds2"));
        _context.SaveChanges();

        Perform(Deserialize(Payload("CleanupDeletedDataset"), split));

        _context.JobIndices.AsNoTracking().Select(j => j.JobId).ToArray().ShouldBe(["other-dataset"]);
        Job.FromExpression<DatasetCleanupService>(s => s.CleanupDeletedAsync(Org, Ref, null)).Method.Name
            .ShouldNotBe(Deserialize(Payload("CleanupDeletedDataset"), split).Method.Name);
    }

    private static Registry.Web.Data.Models.JobIndex Row(string id, string org, string dataset) => new()
    {
        JobId = id, OrgSlug = org, DsSlug = dataset, CurrentState = "Succeeded", CreatedAtUtc = DateTime.UtcNow
    };

    [Test]
    public void RecurringJobsStoredWithOnlyAContext_ResolveToTheirD4Overload()
    {
        foreach (var (type, method) in Recurring)
        {
            var payload = "{\"t\":\"" + type.FullName + ", Registry.Web\",\"m\":\"" + method + "\",\"p\":[\"" + Ctx +
                          "\"],\"a\":[null]}";
            var job = Deserialize(payload, true);

            job.Method.Name.ShouldBe(method);
            job.Method.GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(PerformContext)]);
            // The overload forwards to the one with a token, which the definitions now schedule
            type.GetMethod(method, [typeof(PerformContext), typeof(CancellationToken)]).ShouldNotBeNull();
        }
    }

    [Test]
    public void DdbReadFromJobData_OnlyNamesItsFolder()
    {
        var job = Deserialize(Payload("BuildWrapper"), false);
        var ddb = job.Args[0].ShouldBeAssignableTo<IDDB>()!;

        ddb.DatasetFolderPath.ShouldBe(_folder.Replace('\\', '/'));
        // No native wrapper is created outside DI any more: only the folder name is usable
        Should.Throw<InvalidOperationException>(() => ddb.GetEntry("a"));
    }

    [Test]
    public void NoNewJobStoresADdbObject()
    {
        // The jobs the application queues now carry the dataset's organization and InternalRef
        var job = Job.FromExpression<DdbJobs>(j => j.Build(Org, Ref, "a/b.tif", false, null));
        var payload = InvocationData.SerializeJob(job).SerializePayload();

        payload.ShouldNotContain("Registry.Adapters.DroneDB.DDB");
        payload.ShouldContain(Ref.ToString());
    }

    /// <summary>Releases what the last test left (TearDown already disposes each test's provider).</summary>
    public void Dispose() => _provider?.Dispose();
}
