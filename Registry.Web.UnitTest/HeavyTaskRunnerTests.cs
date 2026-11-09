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

    private sealed class PathTool(string id, Func<Exception?>? validation = null) : HeavyToolBase<PathTool.P>
    {
        public sealed record P(string? Input, string? Output);

        public int Validations { get; private set; }

        public override string Id => id;
        public override string Version => "1";
        public override string Title => id;
        public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
        public override bool ProducesArtifact => false;
        public override JsonDocument InputSchema { get; } = JsonDocument.Parse("{}");

        protected override P ReadParams(ToolParameters p) => new(p.Text("input"), p.Text("output"));

        protected override Task ValidateAsync(HeavyToolRequest request, P p, IHeavyToolValidationContext ctx,
            CancellationToken ct)
        {
            Validations++;
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

    private void Duplicate(string tool, string jobId) =>
        _index.Setup(q => q.FindDedupCandidateAsync(Org, Ds, tool, It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobIndex { JobId = jobId, ToolId = tool, CurrentState = "Processing" });

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
}
