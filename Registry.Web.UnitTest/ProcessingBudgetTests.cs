#nullable enable
using Registry.Web.Services.Jobs;
using System;
using System.Threading;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The processing budget derived from the host, and the routing of native builds to
/// the "builds" queue on every transition to Enqueued.
/// </summary>
[TestFixture]
public class ProcessingBudgetTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [TestCase(4, 8, 1, 4, 4)]
    [TestCase(16, 32, 2, 8, 16)]
    [TestCase(32, 128, 4, 8, 32)]
    [TestCase(32, 8, 2, 8, 32)]    // memory-bound: one heavy worker per 4 GB
    [TestCase(2, 4, 1, 2, 2)]     // small host: at least one heavy and two light workers
    [TestCase(1, 1, 1, 2, 2)]
    public void Derive_DefaultsForHostSizes(int cores, int ramGb, int heavy, int threads, int light)
    {
        var budget = ProcessingBudget.Derive(new HostFacts(cores, ramGb * GiB), null, -1);

        budget.HeavyWorkers.ShouldBe(heavy);
        budget.BuildThreads.ShouldBe(threads);
        budget.LightWorkers.ShouldBe(light);
        budget.BuildCacheMb.ShouldBe(1024);
        budget.BuildWarpMemoryMb.ShouldBe(512);
    }

    [Test]
    public void Derive_ExplicitValuesWin()
    {
        var settings = new ProcessingBudgetSettings
        {
            HeavyWorkers = 3, LightWorkers = 5, BuildThreads = 6, BuildCacheMb = 256, BuildWarpMemoryMb = 128
        };

        ProcessingBudget.Derive(new HostFacts(64, 256 * GiB), settings, 12)
            .ShouldBe(new ProcessingBudget(3, 5, 6, 256, 128));
    }

    [Test]
    public void Derive_ThreadsFollowAnExplicitHeavyWorkerCount()
    {
        var budget = ProcessingBudget.Derive(new HostFacts(16, 64 * GiB),
            new ProcessingBudgetSettings { HeavyWorkers = 8 }, -1);

        budget.BuildThreads.ShouldBe(2, "8 builds x 2 threads = 16 cores");
    }

    [Test]
    public void Derive_LegacyWorkerThreadsSetsLightWorkers()
    {
        ProcessingBudget.Derive(new HostFacts(16, 32 * GiB), null, 6).LightWorkers.ShouldBe(6);
    }

    // ---- Queue routing -------------------------------------------------------------------

    private static readonly BuildQueueRoutingFilter Filter =
        new((toolId, _) => toolId is "build" or "raster-export");

    private static readonly Guid Ref = Guid.NewGuid();

    private static Job BuildJob() => Job.FromExpression<DdbJobs>(j => j.Build("org", Ref, "o.tif", false, null));

    // The shape stored by earlier releases routes the same way
    private static Job LegacyBuildJob() =>
        Job.FromExpression<HangfireUtils>(u => u.BuildWrapper(null!, "o.tif", false, null!));

    private static Job TaskJob(string toolId) =>
        Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(toolId, "1", "{}", null!, CancellationToken.None));

    [Test]
    public void QueueFor_ClassifiesJobs()
    {
        Filter.QueueFor(BuildJob()).ShouldBe(JobQueues.Builds);
        Filter.QueueFor(LegacyBuildJob()).ShouldBe(JobQueues.Builds);
        Filter.QueueFor(Job.FromExpression<DdbJobs>(j => j.MaskBorders("org", Ref, "a", "b", 15, false, null)))
            .ShouldBe(JobQueues.Builds);
        Filter.QueueFor(Job.FromExpression<HangfireUtils>(u => u.MaskBordersWrapper(null!, "a", "b", 15, false, null!)))
            .ShouldBe(JobQueues.Builds);
        Filter.QueueFor(Job.FromExpression<DdbJobs>(j => j.Cleanup("org", Ref, null))).ShouldBeNull();
        Filter.QueueFor(TaskJob("build")).ShouldBe(JobQueues.Builds);
        Filter.QueueFor(TaskJob("raster-export")).ShouldBe(JobQueues.Builds);
        Filter.QueueFor(TaskJob("bulk-download")).ShouldBe(JobQueues.Tasks);
        Filter.QueueFor(Job.FromExpression<HangfireUtils>(u => u.CleanupWrapper(null!, null!))).ShouldBeNull();
        Filter.QueueFor(null).ShouldBeNull();
    }

    private sealed class Storage : IDisposable
    {
        public InMemoryStorage Jobs { get; } = new();
        public BackgroundJobClient Client { get; }

        public Storage()
        {
            var filters = new JobFilterCollection();
            filters.Add(Filter);
            Client = new BackgroundJobClient(Jobs, filters);
        }

        public string? QueueOf(string jobId)
        {
            using var conn = Jobs.GetConnection();
            var state = conn.GetStateData(jobId);
            state.Name.ShouldBe(EnqueuedState.StateName);
            return state.Data.TryGetValue("Queue", out var queue) ? queue : null;
        }

        public void Dispose() => Jobs.Dispose();
    }

    [Test]
    public void Enqueue_BuildGoesToBuildsQueue_EvenWhenAnotherQueueIsRequested()
    {
        using var storage = new Storage();
        var id = storage.Client.Create(BuildJob(), new EnqueuedState(JobQueues.Default));

        storage.QueueOf(id).ShouldBe(JobQueues.Builds);
    }

    [Test]
    public void Retry_RequeuesBuildOnBuildsQueue()
    {
        using var storage = new Storage();
        // A retried job goes Scheduled -> Enqueued with a default EnqueuedState.
        var id = storage.Client.Create(BuildJob(), new ScheduledState(TimeSpan.FromHours(1)));
        storage.Client.ChangeState(id, new EnqueuedState(), ScheduledState.StateName).ShouldBeTrue();

        storage.QueueOf(id).ShouldBe(JobQueues.Builds);
    }

    [Test]
    public void Enqueue_HeavyTaskQueueDependsOnTool()
    {
        using var storage = new Storage();

        storage.QueueOf(storage.Client.Create(TaskJob("build"), new EnqueuedState(JobQueues.Tasks)))
            .ShouldBe(JobQueues.Builds);
        storage.QueueOf(storage.Client.Create(TaskJob("bulk-download"), new EnqueuedState()))
            .ShouldBe(JobQueues.Tasks);
    }

    [Test]
    public void Enqueue_OtherJobsKeepTheirQueue()
    {
        using var storage = new Storage();
        var id = storage.Client.Create(Job.FromExpression<DdbJobs>(j => j.Cleanup("org", Ref, null)),
            new EnqueuedState(JobQueues.Default));

        storage.QueueOf(id).ShouldBe(JobQueues.Default);
    }
}
