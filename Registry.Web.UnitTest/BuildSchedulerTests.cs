using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="BuildScheduler"/> is the one place that queues builds: buildability comes from libddb (the build
/// manifest), the dataset is flagged pending before the first job, entries that arrive start the pending-build retry,
/// and the user a build runs as is resolved once (no site writes <c>user.Id</c> any more).
/// </summary>
[TestFixture]
public class BuildSchedulerTests
{
    private const string Folder = "/datasets/org/ds";

    private readonly List<string> _order = [];
    private readonly List<IndexPayload> _payloads = [];
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private Mock<IPendingBuildFlag> _pending = null!;
    private Mock<IPendingBuildRetry> _retries = null!;
    private Mock<IDDB> _ddb = null!;

    [SetUp]
    public void SetUp()
    {
        _order.Clear();
        _payloads.Clear();
        _jobs = new Mock<IBackgroundJobsProcessor>();
        _jobs.Setup(j => j.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Action<DdbJobs>>, IndexPayload>((_, p) =>
            {
                _order.Add("job");
                _payloads.Add(p);
            })
            .Returns(() => "job-" + _payloads.Count);
        _pending = new Mock<IPendingBuildFlag>();
        _pending.Setup(p => p.SetDatasetHasPendingBuilds("org", "ds", true))
            .Callback(() => _order.Add("flag")).Returns(Task.CompletedTask);
        _retries = new Mock<IPendingBuildRetry>();
        _retries.Setup(r => r.StartIfRetryableAsync(It.IsAny<BuildTarget>()))
            .Callback(() => _order.Add("retry")).ReturnsAsync("coordinator");
        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        _ddb.Setup(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()))
            .Returns((IReadOnlyList<string> paths) => paths
                .Select(p => p.EndsWith(".tif", StringComparison.Ordinal)
                    ? TestBuilds.Build(Folder, p, EntryType.GeoRaster, "h-" + p, BuildState.None)
                    : TestBuilds.NotBuildable(p))
                .ToArray());
    }

    private BuildTarget Target => new(_ddb.Object, new DatasetKey("org", Guid.NewGuid()), "org", "ds");

    private BuildScheduler Scheduler(string? requester = "u") =>
        TestJobs.Scheduler(_jobs.Object, _pending.Object, userId: requester, retries: _retries.Object);

    [Test]
    public async Task QueueBuildable_QueuesTheBuildableOnes_AfterOneFlag()
    {
        var queued = await Scheduler().QueueBuildableAsync(Target, ["a.tif", "b.jpg", "c.tif"]);

        queued.Select(b => b.Path).ShouldBe(["a.tif", "c.tif"]);
        _order.ShouldBe(["flag", "job", "job"]);
        _payloads.Select(p => (p.Path, p.Hash, p.UserId)).ShouldBe([("a.tif", "h-a.tif", "u"), ("c.tif", "h-c.tif", "u")]);
        _ddb.Verify(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>()), Times.Once, "one manifest call");
    }

    [Test]
    public async Task QueueBuildable_NothingBuildable_NeitherFlagsNorQueues()
    {
        (await Scheduler().QueueBuildableAsync(Target, ["b.jpg"])).ShouldBeEmpty();
        _order.ShouldBeEmpty();
    }

    [Test]
    public async Task Origin_NamesTheUserAndTheParentTask()
    {
        await Scheduler().QueueBuildableAsync(Target, ["a.tif"], new BuildOrigin(null, "task-7"));

        _payloads.Single().UserId.ShouldBeNull();
        _payloads.Single().ParentJobId.ShouldBe("task-7");
    }

    [Test]
    public async Task WithoutAUser_TheBuildRunsAsTheService()
    {
        // Push and the remote import wrote user.Id: a request without a user threw
        await Scheduler(requester: null).QueueAsync(Target, "a.tif", "h");

        _payloads.Single().UserId.ShouldBeNull();
    }

    [Test]
    public async Task QueueAdded_QueuesTheBuildableOnes_ThenStartsThePendingRetryOnce()
    {
        var target = Target;

        var queued = await Scheduler().QueueAddedAsync(target, ["a.tif", "b.jpg"]);

        queued.Select(b => b.Path).ShouldBe(["a.tif"]);
        _order.ShouldBe(["flag", "job", "retry"]);
        _retries.Verify(r => r.StartIfRetryableAsync(target), Times.Once);
    }

    /// <summary>
    /// A file nothing builds (the .mtl of an .obj that waits for it) queues no build whose end would retry the pending
    /// builds: its arrival starts the retry itself (red before: only the hourly sweep found it).
    /// </summary>
    [Test]
    public async Task QueueAdded_NothingBuildable_StillStartsThePendingRetry()
    {
        (await Scheduler().QueueAddedAsync(Target, ["model.mtl"])).ShouldBeEmpty();

        _order.ShouldBe(["retry"]);
    }

    [Test]
    public async Task RetryPending_IsTheRetryOfTheDataset()
    {
        var target = Target;

        (await Scheduler().RetryPendingAsync(target)).ShouldBe("coordinator");

        _retries.Verify(r => r.StartIfRetryableAsync(target), Times.Once);
        _payloads.ShouldBeEmpty("the retry queues its own coordinator");
    }

    [Test]
    public async Task QueueDataset_FlagsThenQueuesTheWholeDataset()
    {
        await Scheduler().QueueDatasetAsync(Target, force: true);

        _order.ShouldBe(["flag", "job"]);
        _payloads.Single().Path.ShouldBeNull();
    }

    [TestCase(false, nameof(DdbJobs.Build))]
    [TestCase(true, nameof(DdbJobs.RebuildInBackground))]
    public void QueueDeferred_QueuesABuildOrABackgroundRebuild_WithoutTouchingTheFlag(bool background, string method)
    {
        var payload = new IndexPayload("org", "ds", "h", "service", null, "a.tif");
        var methods = new List<string>();
        _jobs.Setup(j => j.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), payload))
            .Callback<Expression<Action<DdbJobs>>, IndexPayload>((job, _) =>
                methods.Add(((MethodCallExpression)job.Body).Method.Name))
            .Returns("job-1");

        Scheduler().QueueDeferred(new DatasetKey("org", Guid.NewGuid()), "a.tif", background, payload).ShouldBe("job-1");

        methods.ShouldBe([method]);
        _pending.Verify(p => p.SetDatasetHasPendingBuilds(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()),
            Times.Never, "the entry was found through the flag");
    }
}
