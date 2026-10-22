#nullable enable
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
/// RQ-12: <see cref="IBuildScheduler"/> is the one place that queues builds: buildability and deferred builds come
/// from libddb (the build manifest, <see cref="IDdbBuild.IsBuildPending"/>), the dataset is flagged pending before
/// the first job, and the user a build runs as is resolved once (no site writes <c>user.Id</c> any more).
/// </summary>
[TestFixture]
public class BuildSchedulerTests
{
    private const string Folder = "/datasets/org/ds";

    private readonly List<string> _order = [];
    private readonly List<IndexPayload> _payloads = [];
    private Mock<IBackgroundJobsProcessor> _jobs = null!;
    private Mock<IPendingBuildFlag> _pending = null!;
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
        TestJobs.Scheduler(_jobs.Object, _pending.Object, userId: requester);

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
    public async Task QueuePending_OnlyWhenLibddbReportsDeferredBuilds()
    {
        (await Scheduler().QueuePendingAsync(Target)).ShouldBeNull();

        _ddb.Setup(d => d.IsBuildPending()).Returns(true);
        (await Scheduler().QueuePendingAsync(Target)).ShouldNotBeNull();
        _payloads.Single().Path.ShouldBeNull();
    }

    [Test]
    public async Task QueueDataset_FlagsThenQueuesTheWholeDataset()
    {
        await Scheduler().QueueDatasetAsync(Target, force: true);

        _order.ShouldBe(["flag", "job"]);
        _payloads.Single().Path.ShouldBeNull();
    }
}
