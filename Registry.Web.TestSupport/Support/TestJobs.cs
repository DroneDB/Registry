#nullable enable
using Hangfire;
using Hangfire.InMemory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Builds;

namespace Registry.Web.Test.Support;

/// <summary>
/// The background-job services of a test, built the way DI builds them: on a job storage of the
/// test's own (an in-memory one unless given), never on <c>JobStorage.Current</c>.
/// </summary>
public static class TestJobs
{
    /// <summary>A fresh in-memory job storage.</summary>
    public static JobStorage NewStorage() => new InMemoryStorage();

    /// <summary>The pending-build service over the test's collaborators.</summary>
    public static BuildPendingService BuildPendingService(RegistryContext context, IDdbManager ddbManager,
        IBackgroundJobsProcessor jobs, ICacheManager cache, JobStorage? storage = null) =>
        new(context, new DatasetSweep(context), ddbManager, jobs, cache, storage ?? NewStorage(),
            NullLogger<BuildPendingService>.Instance);

    /// <summary>
    /// The dataset jobs (<see cref="Services.Jobs.DdbJobs"/>) over loose mocks unless given: what a heavy
    /// tool's <c>Builds</c> runs inline.
    /// </summary>
    public static DdbJobs DdbJobs(IDdbManager? ddbManager = null, IBackgroundJobsProcessor? jobs = null,
        JobStorage? storage = null)
    {
        var manager = ddbManager ?? Mock.Of<IDdbManager>();
        var pending = new PendingBuildEnqueuer(jobs ?? Mock.Of<IBackgroundJobsProcessor>(), Mock.Of<IJobIndexQuery>(),
            manager, storage ?? NewStorage(), System.TimeProvider.System);
        return new DdbJobs(manager, pending, new Registry.Adapters.FileSystem(), NullLogger<DdbJobs>.Instance);
    }

    /// <summary>A heavy-tool execution context for <paramref name="ddb"/> of org/<paramref name="internalRef"/>.</summary>
    public static Services.HeavyTasks.Adapters.HeavyToolExecutionContext ExecutionContext(Registry.Ports.DroneDB.IDDB ddb,
        string orgSlug = "org", System.Guid internalRef = default, string taskId = "task-1", string? workDir = null,
        IBackgroundJobsProcessor? jobs = null, IDatasetBuilds? builds = null) =>
        new(ddb, new DatasetKey(orgSlug, internalRef == default ? System.Guid.NewGuid() : internalRef), null,
            NullLogger.Instance, taskId, workDir, null!, Scheduler(jobs), builds ?? DdbJobs(jobs: jobs));

    /// <summary>
    /// The build scheduler over <paramref name="jobs"/> (loose mocks unless given), libddb's manifest through the
    /// fake's entry types; builds queued without an origin run as <paramref name="userId"/>.
    /// </summary>
    public static BuildScheduler Scheduler(IBackgroundJobsProcessor? jobs = null, IPendingBuildFlag? pending = null,
        IBuildArtifactResolver? artifacts = null, string? userId = null) =>
        new(artifacts ?? new BuildArtifactResolver(new Registry.Adapters.Testing.FakeDdbWrapper()),
            jobs ?? Mock.Of<IBackgroundJobsProcessor>(), pending ?? Mock.Of<IPendingBuildFlag>(),
            Mock.Of<IBuildRequester>(r => r.UserIdAsync() == System.Threading.Tasks.Task.FromResult(userId)),
            NullLogger<BuildScheduler>.Instance);

    /// <summary>
    /// What a task does with the files it wrote (index, queue builds, invalidate the caches), over a loose
    /// cache invalidator unless given.
    /// </summary>
    public static Services.HeavyTasks.TaskFileIndexer FileIndexer(IDatasetCacheInvalidator? caches = null) =>
        new(new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddSingleton(caches ?? Mock.Of<IDatasetCacheInvalidator>())
                .BuildServiceProvider()
                .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            NullLogger<Services.HeavyTasks.TaskFileIndexer>.Instance);

    /// <summary>The OGC cache generations on a memory cache of their own.</summary>
    public static OgcCacheKeys OgcCacheKeys() => TestCaches.OgcKeys();

    /// <summary>The dataset cache invalidator over <paramref name="cache"/> (OGC keys on a memory cache).</summary>
    public static DatasetCacheInvalidator CacheInvalidator(ICacheManager cache, JobStorage? storage = null) =>
        new(NullLogger<DatasetCacheInvalidator>.Instance, cache, OgcCacheKeys(), storage ?? NewStorage());
}
