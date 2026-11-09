using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Managers;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// U-302, the task rows (FR-XC-15, D-024, D-037, D-038): the actions a task summary offers its reader
/// (<see cref="TaskActions"/>, FR-PRC-21), the label of its target (FR-PRC-22, FR-PRC-43), the wait on a processing
/// node and its state filter (FR-PRC-37), the user who cancelled it (FR-PRC-20) and the queued state of a retry at
/// once (FR-PRC-19).
/// </summary>
[TestFixture]
public class TaskRowsTests
{
    private const string Org = "org";
    private const string Ds = "ds";

    private RegistryContext _db = null!;

    [SetUp]
    public void SetUp() => _db = TestDb.CreateContext<RegistryContext>();

    [TearDown]
    public void TearDown() => _db.Dispose();

    private JobIndex Add(string id, string state, string? userId = null, Action<JobIndex>? set = null)
    {
        var row = new JobIndex
        {
            JobId = id, OrgSlug = Org, DsSlug = Ds, ToolId = "merge-multispectral", ToolVersion = "1",
            CurrentState = state, UserId = userId, CreatedAtUtc = DateTime.UtcNow, LastStateChangeUtc = DateTime.UtcNow
        };
        set?.Invoke(row);
        _db.JobIndices.Add(row);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return row;
    }

    private JobIndex Row(string id) => _db.JobIndices.AsNoTracking().Single(x => x.JobId == id);

    private JobIndexWriter Writer() => new(_db, NullLogger<JobIndexWriter>.Instance, Mock.Of<IRecordCache>());

    // ---- The actions of a row (D-024, D-033, D-037) ----

    [TestCase("Processing", true, true, new[] { "viewLog", "cancel" })]
    [TestCase("Enqueued", true, true, new[] { "viewLog", "cancel" })]
    [TestCase("Failed", true, true, new[] { "viewLog", "retry", "delete" })]
    [TestCase("Succeeded", true, true, new[] { "viewLog", "delete" })]
    [TestCase("Deleted", true, true, new[] { "viewLog", "delete" })]
    [TestCase("Processing", true, false, new[] { "viewLog" })]
    [TestCase("Failed", true, false, new[] { "viewLog" })]
    [TestCase("Processing", false, true, new string[0])]
    [TestCase("Failed", false, false, new string[0])]
    public void Actions_FollowTheStateAndTheCallersRights(string state, bool mayTouch, bool canWrite, string[] expected) =>
        TaskActions.For(state, mayTouch, canWrite).ShouldBe(expected);

    [Test]
    public void TheCaller_TouchesItsOwnTasks_SystemTasks_AndEveryTaskOfADatasetItOwns()
    {
        var member = new TaskCaller("u1", OwnsDataset: false, CanWrite: true);
        member.MayTouch("u1").ShouldBeTrue();
        member.MayTouch(null).ShouldBeTrue("a system task");
        member.MayTouch("u2").ShouldBeFalse();
        new TaskCaller("u1", OwnsDataset: true, CanWrite: true).MayTouch("u2").ShouldBeTrue();
        new TaskCaller(null, OwnsDataset: false, CanWrite: false).MayTouch("u2").ShouldBeFalse();
    }

    // ---- The task list ----

    private TaskHistory History(User? user, bool ownsDataset, bool canWrite, TaskUserNames? names = null)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.GetPendingBuildInfo()).Returns([]);
        var dataset = new Dataset { Slug = Ds, Organization = new Organization { Slug = Org } };
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync(Org, Ds, It.IsAny<AccessType>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatasetAccess(dataset, () => ddb.Object));
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(user!);
        auth.Setup(a => a.IsOwnerOrAdmin(It.IsAny<Dataset>())).ReturnsAsync(ownsDataset);
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), AccessType.Write)).ReturnsAsync(canWrite);
        // The canonical page is cached, for every reader of the dataset: the actions are the caller's all the same
        var cache = new ServiceCollection().AddLogging().AddDistributedMemoryCache().AddRecordCache()
            .BuildServiceProvider().GetRequiredService<IRecordCache>();
        return new TaskHistory(access.Object, new JobIndexQuery(_db), cache,
            new TaskAuthorizer(access.Object, new JobIndexQuery(_db), auth.Object), null!,
            TestJobs.BuildTaskWaits(_db), TestSettings.Options<ProcessingPlatformSettings>(new AppSettings()), names);
    }

    private static Mock<TaskUserNames> Names()
    {
        var names = new Mock<TaskUserNames>(MockBehavior.Loose, [null!]);
        names.Setup(n => n.ResolveAsync(It.IsAny<System.Collections.Generic.IEnumerable<string?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Collections.Generic.Dictionary<string, string> { ["u-admin"] = "admin" });
        return names;
    }

    [Test]
    public async Task AReadOnlyMember_GetsNoActionOnTheTasksOfOthers_AndNoCancelOrRetry()
    {
        Add("running", "Processing", userId: "owner");
        Add("failed", "Failed", userId: "owner");
        Add("build", "Failed");

        var page = (await History(new User { Id = "reader" }, ownsDataset: false, canWrite: false)
            .ListPageAsync(Org, Ds, null, null, 0, 50)).Items;

        page.Single(t => t.TaskId == "running").Actions.ShouldBeEmpty();
        page.Single(t => t.TaskId == "failed").Actions.ShouldBeEmpty();
        // A system task: its log, which the server lets every reader of the dataset read (D-033)
        page.Single(t => t.TaskId == "build").Actions.ShouldBe(["viewLog"]);
    }

    [Test]
    public async Task TheOwnerOfTheDataset_GetsTheActionsOfEachState()
    {
        Add("running", "Processing", userId: "someone");
        Add("failed", "Failed", userId: "someone");
        Add("cancelled", "Deleted", userId: "someone", r => r.CancelledBy = "u-admin");

        var page = (await History(new User { Id = "u-admin" }, ownsDataset: true, canWrite: true)
            .ListPageAsync(Org, Ds, null, null, 0, 50)).Items;

        page.Single(t => t.TaskId == "running").Actions.ShouldBe(["viewLog", "cancel"]);
        page.Single(t => t.TaskId == "failed").Actions.ShouldBe(["viewLog", "retry", "delete"]);
        // A cancelled task gets no Retry: the user starts the tool again (D-037)
        page.Single(t => t.TaskId == "cancelled").Actions.ShouldBe(["viewLog", "delete"]);
    }

    [Test]
    public async Task TheSummary_CarriesTheLabel_TheWait_AndWhoCancelled()
    {
        Add("merge", "Processing", set: r => { r.TargetLabel = "→ out.tif"; r.WaitingForNode = true; });
        Add("cancelled", "Deleted", set: r => { r.CancelledBy = "u-admin"; r.ProgressPercent = 42; });
        Add("synced", "Deleted");

        var page = (await History(null, false, false, Names().Object).ListPageAsync(Org, Ds, null, null, 0, 50)).Items;

        page.Single(t => t.TaskId == "merge").ShouldSatisfyAllConditions(
            t => t.TargetLabel.ShouldBe("→ out.tif"),
            t => t.WaitingForNode.ShouldBeTrue());
        page.Single(t => t.TaskId == "cancelled").ShouldSatisfyAllConditions(
            t => t.CancelledBy.ShouldBe("admin"),
            t => t.ProgressPercent.ShouldBe(42));
        // A Deleted task that no user cancelled (job sync, recoveries, clean-up) keeps no name: its label stays Deleted
        page.Single(t => t.TaskId == "synced").CancelledBy.ShouldBeNull();
    }

    [Test]
    public async Task TheStatusOfACancelledTask_SaysWhoCancelledIt_InItsLog()
    {
        Add("cancelled", "Deleted", set: r => r.CancelledBy = "u-admin");

        var status = await History(new User { Id = "u-admin" }, true, true, Names().Object)
            .GetStatusAsync(Org, Ds, "cancelled");

        status.CancelledBy.ShouldBe("admin");
        status.LogTail.Last().ShouldEndWith("Cancelled by admin");
        status.Actions.ShouldBe(["viewLog", "delete"]);
    }

    [Test]
    public async Task ATaskOfSomebodyElse_IsRefusedWithASentence()
    {
        Add("theirs", "Failed", userId: "owner");

        var ex = await Should.ThrowAsync<Registry.Web.Exceptions.ForbiddenException>(() =>
            History(new User { Id = "reader" }, false, true).GetStatusAsync(Org, Ds, "theirs"));
        ex.Message.ShouldBe(TaskAuthorizer.NotYoursMessage);
    }

    // ---- The state filter: a task queued on a node is Enqueued (FR-PRC-37) ----

    [Test]
    public async Task TheEnqueuedFilter_ReturnsATaskQueuedOnANode_AndTheProcessingFilterLeavesItOut()
    {
        Add("queued", "Enqueued");
        Add("on-node", "Processing", set: r => r.WaitingForNode = true);
        Add("running", "Processing");
        var query = new JobIndexQuery(_db);

        (await query.QueryAsync(new JobIndexQueryFilter(Org, Ds, State: "Enqueued"))).Select(j => j.JobId)
            .ShouldBe(["queued", "on-node"], ignoreOrder: true);
        (await query.QueryAsync(new JobIndexQueryFilter(Org, Ds, State: "Processing"))).Select(j => j.JobId)
            .ShouldBe(["running"]);
        (await query.QueryGlobalAsync(new JobIndexGlobalQueryFilter(null, "Enqueued", null, 0, 50)))
            .Select(j => j.JobId).ShouldBe(["queued", "on-node"], ignoreOrder: true);
        (await query.CountGlobalAsync(null, "Processing", null)).ShouldBe(1);
    }

    // ---- The record (JobIndexWriter) ----

    [Test]
    public async Task ARequeue_ShowsTheTaskEnqueuedAtOnce_AndClearsWhatTheFailedRunLeft()
    {
        Add("failed", "Failed", set: r =>
        {
            r.ErrorType = "X"; r.ErrorMessage = "Boom"; r.ProgressPercent = 40; r.WaitingForNode = true;
            r.CancelledBy = "u1"; r.FailedAtUtc = DateTime.UtcNow; r.TargetLabel = "→ out.tif";
        });

        await Writer().ResetForRequeueAsync("failed");

        Row("failed").ShouldSatisfyAllConditions(
            r => r.CurrentState.ShouldBe("Enqueued"),
            r => r.ErrorType.ShouldBeNull(),
            r => r.ErrorMessage.ShouldBeNull(),
            r => r.ProgressPercent.ShouldBeNull(),
            r => r.FailedAtUtc.ShouldBeNull(),
            r => r.WaitingForNode.ShouldBeFalse(),
            r => r.CancelledBy.ShouldBeNull(),
            r => r.TargetLabel.ShouldBe("→ out.tif"));
    }

    [Test]
    public async Task ARequeue_NeverMovesBackARunThatStarted()
    {
        Add("running", "Processing", set: r => r.ProgressPercent = 10);

        await Writer().ResetForRequeueAsync("running");

        Row("running").ShouldSatisfyAllConditions(
            r => r.CurrentState.ShouldBe("Processing"),
            r => r.ProgressPercent.ShouldBe(10));
    }

    [Test]
    public async Task TheWaitOnANode_IsSetOnAProcessingTask_AndItsEndStartsTheProcessing()
    {
        var created = DateTime.UtcNow.AddMinutes(-10);
        Add("photo", "Processing", set: r => r.ProcessingAtUtc = created);
        var writer = Writer();

        (await writer.SetWaitingForNodeAsync("photo", true, DateTime.UtcNow)).ShouldBeTrue();
        Row("photo").WaitingForNode.ShouldBeTrue();

        var started = DateTime.UtcNow;
        (await writer.SetWaitingForNodeAsync("photo", false, started)).ShouldBeTrue();
        Row("photo").ShouldSatisfyAllConditions(
            r => r.WaitingForNode.ShouldBeFalse(),
            r => r.ProcessingAtUtc!.Value.ShouldBe(started, TimeSpan.FromMilliseconds(1)));

        // Running again: no second start
        (await writer.SetWaitingForNodeAsync("photo", false, DateTime.UtcNow.AddMinutes(1))).ShouldBeFalse();
    }

    [Test]
    public async Task TheWaitOnANode_IsNotSetOnAnEndedTask_AndANewStateEndsIt()
    {
        Add("done", "Succeeded");
        Add("photo", "Processing", set: r => r.WaitingForNode = true);
        var writer = Writer();

        (await writer.SetWaitingForNodeAsync("done", true, DateTime.UtcNow)).ShouldBeFalse();
        await writer.UpdateStateAsync("photo", DeletedState.StateName, DateTime.UtcNow);

        Row("photo").WaitingForNode.ShouldBeFalse();
    }

    [Test]
    public async Task WhoCancelled_IsSetOnlyOnAnActiveTask_AndStaysOnceItIsDeleted()
    {
        Add("running", "Processing");
        Add("done", "Succeeded");
        Add("cancelled", "Deleted", set: r => r.CancelledBy = "u1");
        var writer = Writer();

        (await writer.SetCancelledByAsync("running", "u1")).ShouldBeTrue();
        Row("running").CancelledBy.ShouldBe("u1");
        (await writer.SetCancelledByAsync("done", "u1")).ShouldBeFalse();
        (await writer.SetCancelledByAsync("cancelled", null)).ShouldBeFalse();
        Row("cancelled").CancelledBy.ShouldBe("u1");
        (await writer.SetCancelledByAsync("running", null)).ShouldBeTrue();
        Row("running").CancelledBy.ShouldBeNull();
    }

    // ---- The cancel (TaskLifecycle, D-038) ----

    private TaskLifecycle Lifecycle(Mock<IBackgroundJobsProcessor> processor, string userId)
    {
        var dataset = new Dataset { Slug = Ds, Organization = new Organization { Slug = Org } };
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync(Org, Ds, It.IsAny<AccessType>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatasetAccess(dataset, Mock.Of<IDDB>));
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = userId });
        auth.Setup(a => a.IsOwnerOrAdmin(It.IsAny<Dataset>())).ReturnsAsync(true);
        return new TaskLifecycle(access.Object, new TaskAuthorizer(access.Object, new JobIndexQuery(_db), auth.Object),
            processor.Object, Writer(), null!, NullLogger<TaskLifecycle>.Instance);
    }

    [Test]
    public async Task TheCancelOfAUser_RecordsWhoCancelled()
    {
        Add("running", "Processing", userId: "owner");
        var processor = new Mock<IBackgroundJobsProcessor>();
        processor.Setup(p => p.Delete("running")).Returns(true);

        (await Lifecycle(processor, "u-admin").CancelAsync(Org, Ds, "running")).ShouldBeTrue();

        Row("running").CancelledBy.ShouldBe("u-admin");
    }

    [Test]
    public async Task TheCancelOfATaskWhoseJobIsGone_EndsItDeleted_WithWhoCancelled()
    {
        Add("lost", "Enqueued", userId: "owner");
        var processor = new Mock<IBackgroundJobsProcessor>();
        processor.Setup(p => p.Delete("lost")).Returns(false);

        (await Lifecycle(processor, "u-admin").CancelAsync(Org, Ds, "lost")).ShouldBeTrue();

        Row("lost").ShouldSatisfyAllConditions(
            r => r.CurrentState.ShouldBe("Deleted"),
            r => r.CancelledBy.ShouldBe("u-admin"));
    }

    // ---- Labels (FR-PRC-22, FR-PRC-43) ----

    private static HeavyToolRequest Request(string toolId, string? path, object parameters) =>
        new(toolId, "1", Org, Ds, path, JsonSerializer.SerializeToElement(parameters));

    [Test]
    public void APhotogrammetryRun_IsLabelledByItsName_Folder_Output_AndProfile()
    {
        var tool = new PhotogrammetryTool(null!, null!, null!,
            TestSettings.Options<ImportSettings>(new AppSettings()),
            TestSettings.Options<ProcessingPlatformSettings>(new AppSettings()), TestPaths.Policy, null!);

        tool.Label(Request("photogrammetry", null, new
        {
            name = "limit-1", folder = "images", destPath = "photogrammetry_output", profile = "Fast Orthophoto"
        })).ShouldBe("limit-1 · images/ → photogrammetry_output (Fast Orthophoto)");
        tool.Label(Request("photogrammetry", null, new { createNewDataset = true, newDatasetName = "run-2" }))
            .ShouldBe("→ new dataset run-2");
    }

    [Test]
    public void ABuildWithoutAnEntry_IsThePendingBuildsCheck()
    {
        var tool = new BuildTool(Mock.Of<IBuildOutcomeClassifier>());
        tool.Label(Request("build", null, new { })).ShouldBe("Pending builds check");
        tool.Label(Request("build", "a.tif", new { })).ShouldBeNull();
    }
}
