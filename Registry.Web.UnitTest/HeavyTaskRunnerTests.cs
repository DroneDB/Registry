using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Managers;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The submit checks of every tool in <see cref="HeavyTaskRunner.CheckSubmitAsync"/> (FR-XC-25, D-013), with two
/// tools that declare their paths: an output that is an input (400, before the tool's validation), the deduplication
/// (a second submit gets the first task's id, even though that task writes the output), an output that an unfinished
/// task of the dataset writes (409, the task's own record excluded), the quota, the submit lock (a busy lock is a 503
/// with Retry-After) and the task record of the enqueue (the output paths and the label).
/// </summary>
[TestFixture]
public class HeavyTaskRunnerTests
{
    private const string Org = "org";
    private const string Ds = "ds";

    private Mock<IJobIndexQuery> _index = null!;
    private Mock<IBackgroundJobsProcessor> _processor = null!;
    private InMemoryStorage _storage = null!;
    private List<IndexPayload> _enqueued = null!;
    private ProcessingPlatformSettings _settings = null!;
    private Dictionary<string, IHeavyTool> _tools = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = new InMemoryStorage();
        _enqueued = [];
        _settings = new ProcessingPlatformSettings { DedupEnabled = true };

        _index = new Mock<IJobIndexQuery>();
        _index.Setup(q => q.GetActiveOutputsAsync(Org, Ds, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        _processor = new Mock<IBackgroundJobsProcessor>();
        _processor
            .Setup(p => p.EnqueueIndexed(It.IsAny<Expression<Func<HeavyTaskJobWrapper, Task>>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Func<HeavyTaskJobWrapper, Task>>, IndexPayload>((_, p) => _enqueued.Add(p))
            .Returns(() => "task-" + _enqueued.Count);

        _tools = new Dictionary<string, IHeavyTool>
        {
            ["writer"] = new PathTool("writer"),
            ["other"] = new PathTool("other")
        };
    }

    [TearDown]
    public void TearDown() => _storage.Dispose();

    // ---- The two tools: they read "input" and write "output", and validate as told ----

    private sealed class PathTool(string id, Func<Exception?>? validation = null, bool producesArtifact = false,
        Action<IHeavyToolValidationContext>? seen = null)
        : HeavyToolBase<PathTool.P>
    {
        public sealed record P(string? Input, string? Output);

        public int Validations { get; private set; }

        public override string Id => id;
        public override string Version => "1";
        public override string Title => id;
        public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
        public override bool ProducesArtifact => producesArtifact;
        public override JsonDocument InputSchema { get; } = JsonDocument.Parse("{}");

        protected override P ReadParams(ToolParameters p) => new(p.Text("input"), p.Text("output"));

        protected override Task ValidateAsync(HeavyToolRequest request, P p, IHeavyToolValidationContext ctx,
            CancellationToken ct)
        {
            Validations++;
            seen?.Invoke(ctx);
            if (validation?.Invoke() is { } error)
                throw error;
            return Task.CompletedTask;
        }

        protected override HeavyToolPlan Plan(HeavyToolRequest request, P p, IHeavyToolValidationContext ctx) =>
            new(10, id, null, null);

        protected override Task<HeavyToolArtifact?> ExecuteAsync(HeavyToolRequest request, P p,
            IHeavyToolExecutionContext ctx, IProgress<HeavyToolProgress> progress, CancellationToken ct) =>
            Task.FromResult<HeavyToolArtifact?>(null);

        protected override IReadOnlyList<string> InputPaths(P p) => p.Input is null ? [] : [TestPaths.Policy.Normalize(p.Input)];

        protected override IReadOnlyList<string> OutputPaths(P p) =>
            p.Output is null ? [] : [TestPaths.Policy.Normalize(p.Output)];

        protected override string? Label(P p) => p.Output is null ? null : "→ " + p.Output;
    }

    private HeavyTaskRunner Runner(HeavyTaskSubmitLock? submitLock = null, IJobIndexQuery? quotaIndex = null)
    {
        var registry = new Mock<IHeavyToolRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((tool, _) => _tools.GetValueOrDefault(tool));

        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        var datasets = new Mock<IDatasetResolver>();
        datasets.Setup(d => d.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(Mock.Of<IDDB>());

        var options = TestSettings.Options<ProcessingPlatformSettings>(
            Microsoft.Extensions.Options.Options.Create(new AppSettings { ProcessingPlatform = _settings }));

        return new HeavyTaskRunner(registry.Object, datasets.Object, ddbManager.Object,
            new HeavyTaskQuota(quotaIndex ?? _index.Object, options), new HeavyTaskDedup(_index.Object, options),
            _index.Object, submitLock ?? new HeavyTaskSubmitLock(_storage), _processor.Object,
            NullLogger<HeavyTaskRunner>.Instance);
    }

    private static HeavyTaskSubmitRequest Submit(string tool, string? input, string? output, string? user = "user") =>
        new(Org, Ds, tool, null, null, JsonSerializer.SerializeToElement(new { input, output }), false, user, null);

    private void ActiveTask(string jobId, string tool, params string[] outputs) =>
        _index.Setup(q => q.GetActiveOutputsAsync(Org, Ds, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ActiveTaskOutputs(jobId, tool, outputs)]);

    // A task of the same request in the history: an unfinished one is also found by the active-only lookup
    private void Duplicate(string tool, string jobId, string state = "Processing")
    {
        var row = new JobIndex { JobId = jobId, ToolId = tool, CurrentState = state };
        _index.Setup(q => q.FindDedupCandidateAsync(Org, Ds, tool, It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(row);
        _index.Setup(q => q.FindActiveDedupCandidateAsync(Org, Ds, tool, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state == "Succeeded" ? null : row);
    }

    // ---- Output is an input ----

    [Test]
    public async Task OutputThatIsAnInput_Is400_BeforeTheToolValidation()
    {
        // The tool would refuse the output as an existing file (409): the runner answers first, with the 400 of D-040
        var tool = new PathTool("writer", () => new NameClashException("bands/b1.tif"));
        _tools["writer"] = tool;

        var ex = await Should.ThrowAsync<BadRequestException>(() =>
            Runner().SubmitAsync(Submit("writer", "bands/b1.tif", "Bands/B1.TIF")));

        ex.Message.ShouldBe("The output 'Bands/B1.TIF' is also one of the input files: choose another output path.");
        ex.StatusCode.ShouldBe(400);
        tool.Validations.ShouldBe(0);
        _enqueued.ShouldBeEmpty();
    }

    // ---- Deduplication ----

    [Test]
    public async Task DuplicateSubmit_ReturnsTheExistingTask_EvenThoughThatTaskWritesTheOutput()
    {
        Duplicate("writer", "first");
        ActiveTask("first", "writer", "out/a.tif");

        var result = await Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif"));

        result.TaskId.ShouldBe("first");
        result.Deduplicated.ShouldBeTrue();
        _enqueued.ShouldBeEmpty();
    }

    [Test]
    public async Task ASucceededTaskOfAToolThatWritesTheDataset_IsNotReused_ANewTaskStarts()
    {
        // A merge that succeeded, whose output the user deleted: the same submit runs again (FR-PRC-02)
        Duplicate("writer", "done", state: "Succeeded");

        var result = await Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif"));

        result.Deduplicated.ShouldBeFalse();
        result.TaskId.ShouldBe("task-1");
        _enqueued.Count.ShouldBe(1);
    }

    [Test]
    public async Task ASucceededTaskOfAToolThatProducesAnArtifact_IsReused()
    {
        _tools["export"] = new PathTool("export", producesArtifact: true);
        Duplicate("export", "done", state: "Succeeded");

        var result = await Runner().SubmitAsync(Submit("export", "in.tif", null));

        result.TaskId.ShouldBe("done");
        result.Deduplicated.ShouldBeTrue();
        _enqueued.ShouldBeEmpty();
        _index.Verify(q => q.FindActiveDedupCandidateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task WithDeduplicationOff_NoTaskIsReused()
    {
        _settings.DedupEnabled = false;
        Duplicate("writer", "running");

        var result = await Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif"));

        result.Deduplicated.ShouldBeFalse();
        _enqueued.Count.ShouldBe(1);
    }

    // ---- An output that an unfinished task writes ----

    [Test]
    public async Task OutputThatAnActiveTaskWrites_Is409_WhateverTheToolAndTheLetterCase()
    {
        ActiveTask("running", "other", "OUT/A.tif");

        var ex = await Should.ThrowAsync<ConflictException>(() =>
            Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif")));

        ex.StatusCode.ShouldBe(409);
        ex.Message.ShouldBe("Another task of this dataset that has not finished writes 'out/a.tif': " +
                            "wait for it to end, or choose another output path.");
        ex.ShouldNotBeOfType<NameClashException>();
        _enqueued.ShouldBeEmpty();
    }

    [Test]
    public async Task OtherOutputsOfActiveTasks_DoNotRefuse()
    {
        ActiveTask("running", "other", "out/b.tif");

        var result = await Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif"));

        result.Deduplicated.ShouldBeFalse();
        _enqueued.Count.ShouldBe(1);
    }

    [Test]
    public async Task ARetry_IsNotRefusedByItsOwnRecord_AndRunsItsOwnRequeue()
    {
        ActiveTask("failed-task", "writer", "out/a.tif");
        Duplicate("writer", "failed-task");
        HeavyTaskAdmission? admitted = null;

        var result = await Runner().CheckSubmitAsync(Submit("writer", "in.tif", "out/a.tif"),
            (admission, _) =>
            {
                admitted = admission;
                return Task.FromResult("failed-task");
            }, ownJobId: "failed-task");

        result.TaskId.ShouldBe("failed-task");
        result.Deduplicated.ShouldBeFalse();
        admitted.ShouldNotBeNull();
        admitted.OutputPaths.ShouldBe(["out/a.tif"]);
        admitted.Label.ShouldBe("→ out/a.tif");
        _enqueued.ShouldBeEmpty();
    }

    // ---- Quota ----

    [Test]
    public async Task Quota_IsCheckedAfterTheOutputs_AndRefusesWith429()
    {
        _settings.MaxConcurrentTasksGlobal = 1;
        _index.Setup(q => q.CountActiveUserTasksAsync(null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var ex = await Should.ThrowAsync<HeavyTaskQuotaException>(() =>
            Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif")));

        ex.StatusCode.ShouldBe(429);
        _enqueued.ShouldBeEmpty();
    }

    [Test]
    public async Task ActiveOutput_IsRefusedBeforeTheQuota()
    {
        ActiveTask("running", "other", "out/a.tif");
        var quotaIndex = new Mock<IJobIndexQuery>(MockBehavior.Strict);

        await Should.ThrowAsync<ConflictException>(() =>
            Runner(quotaIndex: quotaIndex.Object).SubmitAsync(Submit("writer", "in.tif", "out/a.tif")));

        quotaIndex.VerifyNoOtherCalls();
    }

    // ---- The submit lock ----

    [Test]
    public async Task BusySubmitLock_Is503WithRetryAfter_AndEnqueuesNothing()
    {
        var connection = new Mock<IStorageConnection>();
        connection.Setup(c => c.AcquireDistributedLock(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Throws(new DistributedLockTimeoutException("heavy-submit"));
        var storage = new Mock<JobStorage>();
        storage.Setup(s => s.GetConnection()).Returns(connection.Object);

        var ex = await Should.ThrowAsync<TransientException>(() =>
            Runner(new HeavyTaskSubmitLock(storage.Object)).SubmitAsync(Submit("writer", "in.tif", "out/a.tif")));

        ex.Message.ShouldBe(HeavyTaskSubmitLock.BusyMessage);
        var outcome = ApiExceptionClassifier.Classify(ex, authenticated: true);
        outcome.StatusCode.ShouldBe(503);
        outcome.RetryAfterSeconds.ShouldBe(HeavyTaskSubmitLock.RetryAfterSeconds);
        _enqueued.ShouldBeEmpty();
    }

    [Test]
    public void SubmitLock_IsPerDataset_AndHasItsOwnKey()
    {
        var key = DatasetBuildCoordinator.ResourceKey(Org, Ds, DatasetBuildCoordinator.HeavySubmitKeyPrefix);

        key.ShouldStartWith("heavy-submit:");
        key.Length.ShouldBeLessThanOrEqualTo(64);
        key.ShouldNotBe(DatasetBuildCoordinator.ResourceKey(Org, Ds));
        key.ShouldNotBe(DatasetBuildCoordinator.ResourceKey(Org, "other", DatasetBuildCoordinator.HeavySubmitKeyPrefix));
    }

    [Test]
    public async Task SubmitLock_IsHeldFromTheDedupToTheEnqueue()
    {
        var submitLock = new Mock<HeavyTaskSubmitLock>(_storage, null!) { CallBase = true };
        var held = false;
        var lease = new Mock<IDisposable>();
        lease.Setup(l => l.Dispose()).Callback(() => held = false);
        submitLock.Setup(l => l.Acquire(Org, Ds)).Returns(() =>
        {
            held = true;
            return lease.Object;
        });
        var heldDuringEnqueue = false;

        await Runner(submitLock.Object).CheckSubmitAsync(Submit("writer", "in.tif", "out/a.tif"), (_, _) =>
        {
            heldDuringEnqueue = held;
            return Task.FromResult("t");
        });

        heldDuringEnqueue.ShouldBeTrue();
        held.ShouldBeFalse("released after the enqueue");
    }

    // ---- The task record ----

    [Test]
    public async Task Enqueue_StoresTheOutputPathsAndTheLabel()
    {
        await Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif"));

        var payload = _enqueued.ShouldHaveSingleItem();
        payload.OutputPathsJson.ShouldBe("[\"out/a.tif\"]");
        payload.TargetLabel.ShouldBe("→ out/a.tif");
        payload.ToolId.ShouldBe("writer");
    }

    [Test]
    public async Task Enqueue_OfAToolWithoutOutputs_StoresNone()
    {
        await Runner().SubmitAsync(Submit("writer", null, null));

        var payload = _enqueued.ShouldHaveSingleItem();
        payload.OutputPathsJson.ShouldBeNull();
        payload.TargetLabel.ShouldBeNull();
    }

    [Test]
    public async Task Enqueue_CutsALabelLongerThanTheColumn()
    {
        var output = new string('a', 400) + ".tif";

        await Runner().SubmitAsync(Submit("writer", null, output));

        var label = _enqueued.ShouldHaveSingleItem().TargetLabel!;
        label.Length.ShouldBe(HeavyTaskJobs.TargetLabelMaxLength);
        label.ShouldEndWith("…");
    }

    // ---- The retry of a failed task (D-037): the submit checks run first, through CheckSubmitAsync ----

    private const string FailedJob = "failed-merge";

    // The write record of the failed task (TaskWriteRecordStore): a compare-and-swap on one stored record
    private TaskWriteRecord? _record;
    private Mock<ITaskWriteRecordStore> _records = null!;

    private (TaskRetry Retry, Mock<IJobIndexWriter> Writer) RetryOf(string state, string tool = "writer",
        string? published = null, TaskWriteRecord? record = null)
    {
        var job = new JobIndex
        {
            JobId = FailedJob, OrgSlug = Org, DsSlug = Ds, ToolId = tool, ToolVersion = "1", CurrentState = state,
            UserId = "owner"
        };
        _index.Setup(q => q.GetByJobIdAsync(Org, Ds, FailedJob, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var request = new HeavyToolRequest(tool, "1", Org, Ds, null,
            JsonSerializer.SerializeToElement(new { input = "in.tif", output = "out/a.tif" }));
        _processor.Setup(p => p.GetStoredJob(FailedJob)).Returns(new StoredJob(typeof(HeavyTaskJobWrapper),
            nameof(HeavyTaskJobWrapper.Run), [tool, "1", JsonSerializer.Serialize(request), null, CancellationToken.None]));
        _processor.Setup(p => p.GetJobParameter(FailedJob, PublishedOutputMarker.Key)).Returns(published);
        // The live state of the job: Failed until a re-queue is accepted, then queued
        var live = state == "Failed" ? JobStatus.Failed : JobStatus.Unknown;
        _processor.Setup(p => p.GetJobStatus(FailedJob)).Returns(() => live);
        _processor.Setup(p => p.Requeue(FailedJob)).Returns(() =>
        {
            if (live != JobStatus.Failed)
                return false;
            live = JobStatus.Enqueued;
            return true;
        });

        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), Organization = new Organization { Slug = Org } };
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync(Org, Ds, It.IsAny<AccessType>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatasetAccess(dataset, Mock.Of<IDDB>));
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.IsOwnerOrAdmin(It.IsAny<Dataset>())).ReturnsAsync(true);
        var writer = new Mock<IJobIndexWriter>();

        // Without a record: a tool that adds no dataset files, which the retry has none to reset
        _record = record;
        _records = new Mock<ITaskWriteRecordStore>();
        _records.Setup(r => r.ReadAsync(FailedJob, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _record is null ? null : TaskWriteRecord.Parse(_record.ToJson()));
        _records.Setup(r => r.ChangeAsync(FailedJob, It.IsAny<Func<TaskWriteRecord?, TaskWriteRecord?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Func<TaskWriteRecord?, TaskWriteRecord?> change, CancellationToken _) =>
            {
                var changed = change(_record is null ? null : TaskWriteRecord.Parse(_record.ToJson()));
                if (changed is null)
                    return new TaskWriteRecordChange(_record, false, true);
                _record = changed;
                return new TaskWriteRecordChange(changed, true, true);
            });

        return (new TaskRetry(new TaskAuthorizer(access.Object, _index.Object, auth.Object), _processor.Object,
            writer.Object, Mock.Of<IDatasetResolver>(), Mock.Of<IDdbManager>(), _records.Object,
            NullLogger<TaskRetry>.Instance, Runner()), writer);
    }

    // The record of a photogrammetry run that created its dataset and failed: rolled back, its dataset deleted later
    private static readonly TaskCreatedDataset RunDataset =
        new("org", Guid.Parse("4a7e0c1e-58f4-4d8b-9a55-2d3c1f0b9e01"), "run-2");

    private static TaskWriteRecord RolledBackRun() => new()
    {
        State = TaskWriteState.RolledBack, CreatedDataset = RunDataset, CreatedDatasetDeleteJob = "delete-run-2"
    };

    [Test]
    public async Task Retry_OfAFailedTask_RunsTheChecksThenRequeuesItsOwnJob()
    {
        var (retry, writer) = RetryOf("Failed");

        (await retry.RetryAsync(Org, Ds, FailedJob)).ShouldBeTrue();

        ((PathTool)_tools["writer"]).Validations.ShouldBe(1);
        _processor.Verify(p => p.Requeue(FailedJob), Times.Once);
        writer.Verify(w => w.ResetForRequeueAsync(FailedJob, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _enqueued.ShouldBeEmpty();
    }

    [Test]
    public async Task Retry_OfAFailedMergeWhoseOutputAnotherActiveTaskWrites_Is409_AndRequeuesNothing()
    {
        var (retry, writer) = RetryOf("Failed");
        ActiveTask("other-task", "other", "out/a.tif");

        var ex = await Should.ThrowAsync<ConflictException>(() => retry.RetryAsync(Org, Ds, FailedJob));

        ex.StatusCode.ShouldBe(409);
        _processor.Verify(p => p.Requeue(It.IsAny<string>()), Times.Never);
        writer.Verify(w => w.ResetForRequeueAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Retry_ForAnOwnerOverAQuota_IsRefusedByTheQuota_AndRequeuesNothing()
    {
        _settings.MaxConcurrentTasksGlobal = 1;
        _index.Setup(q => q.CountActiveUserTasksAsync(null, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var (retry, _) = RetryOf("Failed");

        await Should.ThrowAsync<HeavyTaskQuotaException>(() => retry.RetryAsync(Org, Ds, FailedJob));

        _processor.Verify(p => p.Requeue(It.IsAny<string>()), Times.Never);
    }

    [TestCase("Succeeded")]
    [TestCase("Deleted")]
    public async Task Retry_OfASucceededOrCancelledTask_Is409(string state)
    {
        var (retry, _) = RetryOf(state);

        var ex = await Should.ThrowAsync<ConflictException>(() => retry.RetryAsync(Org, Ds, FailedJob));

        ex.StatusCode.ShouldBe(409);
        _processor.Verify(p => p.Requeue(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Retry_OfATaskThatWroteItsOutput_ValidatesWithThatOutputPublished()
    {
        string? seen = null;
        _tools["writer"] = new PathTool("writer", seen: ctx => seen = ctx.PublishedOutput);
        var (retry, _) = RetryOf("Failed", published: "out/a.tif");

        await retry.RetryAsync(Org, Ds, FailedJob);

        seen.ShouldBe("out/a.tif");
    }

    [Test]
    public async Task ASubmit_ValidatesWithNoPublishedOutput()
    {
        string? seen = "unset";
        _tools["writer"] = new PathTool("writer", seen: ctx => seen = ctx.PublishedOutput);

        await Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif"));

        seen.ShouldBeNull();
        _processor.Verify(p => p.GetJobParameter(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ---- The retry of a run that created its dataset (D-037, FR-XC-27) ----

    [Test]
    public async Task Retry_OfARunThatCreatedItsDataset_ValidatesWithThatDatasetAsItsOwn()
    {
        TaskCreatedDataset? seen = null;
        _tools["writer"] = new PathTool("writer", seen: ctx => seen = ctx.OwnCreatedDataset);
        var (retry, _) = RetryOf("Failed", record: RolledBackRun());

        (await retry.RetryAsync(Org, Ds, FailedJob)).ShouldBeTrue();

        seen.ShouldBe(new TaskCreatedDataset("org", Guid.Parse("4a7e0c1e-58f4-4d8b-9a55-2d3c1f0b9e01"), "run-2"));
    }

    [Test]
    public async Task ASubmit_ValidatesWithNoOwnCreatedDataset_EvenWhenTheServerCreatedOneForIt()
    {
        TaskCreatedDataset? seen = RunDataset;
        _tools["writer"] = new PathTool("writer", seen: ctx => seen = ctx.OwnCreatedDataset);

        await Runner().SubmitAsync(Submit("writer", "in.tif", "out/a.tif") with { CreatedDataset = RunDataset });

        seen.ShouldBeNull();
    }

    [Test]
    public async Task Retry_OfARunThatCreatedItsDataset_CancelsTheWaitingDeleteOfThatDataset_AndKeepsItInTheRecord()
    {
        _processor.Setup(p => p.Delete("delete-run-2", "Scheduled")).Returns(true);
        var (retry, _) = RetryOf("Failed", record: RolledBackRun());

        (await retry.RetryAsync(Org, Ds, FailedJob)).ShouldBeTrue();

        _processor.Verify(p => p.Delete("delete-run-2", "Scheduled"), Times.Once);
        _processor.Verify(p => p.Delete("delete-run-2"), Times.Never);
        _record.ShouldNotBeNull().ToJson().ShouldBe(
            """{"state":"open","attempt":0,"createdDataset":{"org":"org","internalRef":"4a7e0c1e-58f4-4d8b-9a55-2d3c1f0b9e01","slug":"run-2"},"datasets":[]}""");
    }

    [Test]
    public async Task Retry_RefusedByTheValidation_LeavesTheStateAndTheWriteRecordUntouched()
    {
        _tools["writer"] = new PathTool("writer", () => new ConflictException("A dataset named \"run-2\" already exists"));
        var (retry, writer) = RetryOf("Failed", record: RolledBackRun());
        var before = _record!.ToJson();

        var ex = await Should.ThrowAsync<ConflictException>(() => retry.RetryAsync(Org, Ds, FailedJob));

        ex.Message.ShouldBe("A dataset named \"run-2\" already exists");
        _record!.ToJson().ShouldBe(before);
        _records.Verify(r => r.ChangeAsync(It.IsAny<string>(), It.IsAny<Func<TaskWriteRecord?, TaskWriteRecord?>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.Requeue(It.IsAny<string>()), Times.Never);
        _processor.Verify(p => p.Delete(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        writer.Verify(w => w.ResetForRequeueAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ASecondRetry_OfATaskTheFirstStartedAgain_Is409_AndKeepsTheWriteRecordOfTheNewRun()
    {
        var (retry, writer) = RetryOf("Failed", record: RolledBackRun());
        (await retry.RetryAsync(Org, Ds, FailedJob)).ShouldBeTrue();

        // The new run starts and records what it writes
        _record!.Add(new DatasetKey("org", RunDataset.InternalRef), ["odm_orthophoto/odm_orthophoto.tif"], ["odm_orthophoto"]);
        var ofTheNewRun = _record.ToJson();

        // The second click: its row still read Failed when it was authorized
        var ex = await Should.ThrowAsync<ConflictException>(() => retry.RetryAsync(Org, Ds, FailedJob));

        ex.StatusCode.ShouldBe(409);
        ex.Message.ShouldBe("The task is already running again; only a failed task can be run again");
        _record!.ToJson().ShouldBe(ofTheNewRun);
        // Only the first retry reset the record: the second never touched it, not even for a moment
        _records.Verify(r => r.ChangeAsync(FailedJob, It.IsAny<Func<TaskWriteRecord?, TaskWriteRecord?>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.Requeue(FailedJob), Times.Once);
        writer.Verify(w => w.ResetForRequeueAsync(FailedJob, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Retry_TakesTheRequeueTimeBeforeTheRequeue()
    {
        var (retry, writer) = RetryOf("Failed");
        DateTime? requeuedAt = null;
        DateTime? requeueCalled = null;
        _processor.Setup(p => p.Requeue(FailedJob)).Returns(() =>
        {
            requeueCalled = DateTime.UtcNow;
            return true;
        });
        writer.Setup(w => w.ResetForRequeueAsync(FailedJob, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateTime, CancellationToken>((_, at, _) => requeuedAt = at)
            .Returns(Task.CompletedTask);

        await retry.RetryAsync(Org, Ds, FailedJob);

        requeuedAt.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(requeueCalled!.Value);
    }
}
