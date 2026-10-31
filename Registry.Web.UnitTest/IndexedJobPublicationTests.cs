using Registry.Web.Services.Jobs;
using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Once the creation transaction commits, a publication failure never cancels the job: the
/// publisher returns its id and the job stays in Indexing for recovery.
/// </summary>
[TestFixture]
[NonParallelizable]
public class IndexedJobPublicationTests
{
    private IStateHandler[] _previousHandlers = [];

    [SetUp]
    public void SetUp()
    {
        _previousHandlers = GlobalStateHandlers.Handlers.ToArray();
        IndexingStateHandler.EnsureRegistered();
    }

    [TearDown]
    public void TearDown()
    {
        GlobalStateHandlers.Handlers.Clear();
        foreach (var handler in _previousHandlers) GlobalStateHandlers.Handlers.Add(handler);
    }

    [Test]
    public void IndexWriteFailureAfterCreationReturnsJobIdAndKeepsIndexing()
    {
        var storage = new InMemoryStorage();
        using var storageScope = new JobStorageScope(storage);
        var writer = new Mock<IJobIndexWriter>();
        string? stagedId = null;
        writer.Setup(x => x.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), default))
            .Callback<string, IndexPayload, DateTime, string, System.Threading.CancellationToken>((id, _, _, _, _) =>
            {
                stagedId = id;
                using var connection = storage.GetConnection();
                connection.GetStateData(id).Name.ShouldBe(IndexedJobEnqueuer.PreparationStateName);
            }).ThrowsAsync(new InvalidOperationException("index unavailable"));
        using var provider = new ServiceCollection().AddSingleton(writer.Object).BuildServiceProvider();
        var enqueuer = new IndexedJobEnqueuer(new BackgroundJobClient(storage), provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<ILogger<IndexedJobEnqueuer>>(), storage);
        var jobId = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null),
            new IndexPayload("org", "ds", null, MagicStrings.AutoBuildServiceUserId));
        jobId.ShouldBe(stagedId);
        using var connection = storage.GetConnection();
        connection.GetStateData(jobId).Name.ShouldBe(IndexedJobEnqueuer.PreparationStateName);
        writer.Verify(x => x.UpdateStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), default),
            Times.Never);
    }

    [Test]
    public void RejectedDispatchReturnsJobIdWithoutCancelling()
    {
        var storage = new InMemoryStorage();
        using var storageScope = new JobStorageScope(storage);
        var real = new BackgroundJobClient(storage);
        var client = new Mock<IBackgroundJobClient>();
        string? stagedId = null;
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns<Job, IState>((job, state) =>
            stagedId = real.Create(job, state));
        client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
            .Returns<string, IState, string>((id, state, expected) =>
                state is DeletedState && real.ChangeState(id, state, expected));
        var writer = new Mock<IJobIndexWriter>();
        using var provider = new ServiceCollection().AddSingleton(writer.Object).BuildServiceProvider();
        var enqueuer = new IndexedJobEnqueuer(client.Object, provider.GetRequiredService<IServiceScopeFactory>(), Mock.Of<ILogger<IndexedJobEnqueuer>>(), storage);
        var jobId = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null),
            new IndexPayload("org", "ds", null, MagicStrings.AutoBuildServiceUserId));
        jobId.ShouldBe(stagedId);
        using var connection = storage.GetConnection();
        connection.GetStateData(jobId).Name.ShouldBe(IndexedJobEnqueuer.PreparationStateName);
        client.Verify(x => x.ChangeState(jobId, It.IsAny<DeletedState>(), It.IsAny<string>()), Times.Never);
    }

    // ---------------------------------------------------------------- additional coverage (R1-R7, A2, A5)

    private static IndexPayload FullPayload() =>
        new("org", "ds", "hash-1", "user-1", "pq", "dir/model.obj", "tool-x", "4", "req-1", "parent-1", "wf-1");

    private static IndexedJobEnqueuer EnqueuerWith(PublicationEnv env, IJobIndexWriter? writer = null,
        IBackgroundJobClient? client = null, JobStorage? storage = null)
    {
        var provider = new ServiceCollection().AddSingleton(writer ?? env.Writer).BuildServiceProvider();
        env.Track(provider);
        return new IndexedJobEnqueuer(client ?? env.Client, provider.GetRequiredService<IServiceScopeFactory>(), Mock.Of<ILogger<IndexedJobEnqueuer>>(),
            storage ?? env.Storage);
    }

    private static IJobIndexWriter FailingPrepareWriter()
    {
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(x => x.PrepareForPublicationAsync(It.IsAny<string>(), It.IsAny<IndexPayload>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), default))
            .ThrowsAsync(new InvalidOperationException("index unavailable"));
        return writer.Object;
    }

    [Test]
    public void HandlerNotRegistered_Throws_AndCreatesNoJob()
    {
        using var env = new PublicationEnv();
        GlobalStateHandlers.Handlers.Clear();
        var client = new Mock<IBackgroundJobClient>(MockBehavior.Strict);
        var enqueuer = EnqueuerWith(env, client: client.Object);

        Should.Throw<InvalidOperationException>(() =>
            enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload()));
        Should.Throw<InvalidOperationException>(() =>
            enqueuer.Schedule<HealthCheckJob>(j => j.Ping(null), FullPayload(), TimeSpan.FromMinutes(1)));

        client.VerifyNoOtherCalls();
        env.JobCount().ShouldBe(0);
        env.SetSize().ShouldBe(0);
    }

    [Test]
    public void InvalidPayload_ThrowsBeforeAnyJobIsCreated()
    {
        using var env = new PublicationEnv();
        var enqueuer = EnqueuerWith(env);

        Should.Throw<ArgumentException>(() =>
            enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), new IndexPayload("", "ds", null, null)));

        env.JobCount().ShouldBe(0);
    }

    [Test]
    public void FailureAtCreation_IsReportedToTheCaller_AndNothingIsPublished()
    {
        using var env = new PublicationEnv();
        var writer = new Mock<IJobIndexWriter>(MockBehavior.Strict);
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("commit failed"));
        var enqueuer = EnqueuerWith(env, writer.Object, client.Object);

        Should.Throw<InvalidOperationException>(() =>
            enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload()));

        writer.VerifyNoOtherCalls();
        client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void EmptyJobIdFromClient_IsTreatedAsNotCreated()
    {
        using var env = new PublicationEnv();
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns((string)null!);
        var enqueuer = EnqueuerWith(env, client: client.Object);

        Should.Throw<InvalidOperationException>(() =>
            enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload()));
    }

    [Test]
    public void IndexingState_CarriesCompleteRecord_WithQueueAndAbsoluteScheduledTime()
    {
        using var env = new PublicationEnv();
        var enqueuer = EnqueuerWith(env, FailingPrepareWriter());   // leaves the job in Indexing

        var immediate = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload());
        var scheduled = enqueuer.Schedule<HealthCheckJob>(j => j.Ping(null), FullPayload(), TimeSpan.FromMinutes(45));

        var imm = IndexedJobPublicationRecord.TryParse(env.StateData(immediate)!.Data);
        imm.Status.ShouldBe(IndexedJobRecordParseStatus.Valid);
        imm.Record!.Payload.ShouldBe(FullPayload());
        imm.Record.Destination.ShouldBe(IndexedJobDestination.Immediate);
        imm.Record.Queue.ShouldBe("pq", "the resolved EnqueuedState queue is the payload queue");
        imm.Record.CreatedAtUtc.ShouldBeInRange(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(1));

        var sch = IndexedJobPublicationRecord.TryParse(env.StateData(scheduled)!.Data);
        sch.Record!.Destination.ShouldBe(IndexedJobDestination.Scheduled);
        sch.Record.Payload.ShouldBe(FullPayload());
        sch.Record.EnqueueAtUtc.ShouldBe(sch.Record.CreatedAtUtc.AddMinutes(45));
        env.State(immediate).ShouldBe("Indexing");
        env.State(scheduled).ShouldBe("Indexing");
        env.InSet(immediate).ShouldBeTrue();
        env.InSet(scheduled).ShouldBeTrue();
    }

    [Test]
    public void DefaultQueue_IsRecorded_WhenPayloadHasNoQueue()
    {
        using var env = new PublicationEnv();
        var enqueuer = EnqueuerWith(env, FailingPrepareWriter());

        var id = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), new IndexPayload("o", "d", null, null));

        IndexedJobPublicationRecord.TryParse(env.StateData(id)!.Data).Record!.Queue.ShouldBe("default");
    }

    [Test]
    public void SuccessfulImmediatePublish_EnqueuesOnOriginalQueue_WritesParamsAndRow_RemovesMember()
    {
        using var env = new PublicationEnv();
        var enqueuer = EnqueuerWith(env);

        var id = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload());

        var state = env.StateData(id)!;
        state.Name.ShouldBe("Enqueued");
        state.Data["Queue"].ShouldBe("pq");
        env.InSet(id).ShouldBeFalse();
        env.Param(id, JobParamKeys.OrgSlug).ShouldBe("org");
        env.Param(id, JobParamKeys.DsSlug).ShouldBe("ds");
        env.Param(id, JobParamKeys.Path).ShouldBe("dir/model.obj");
        env.Param(id, JobParamKeys.Hash).ShouldBe("hash-1");
        env.Param(id, JobParamKeys.UserId).ShouldBe("user-1");
        env.Param(id, JobParamKeys.Queue).ShouldBe("pq");
        var row = env.Row(id)!;
        row.ToolId.ShouldBe("tool-x");
        row.ToolVersion.ShouldBe("4");
        row.RequestHash.ShouldBe("req-1");
        row.ParentJobId.ShouldBe("parent-1");
        row.WorkflowExecutionId.ShouldBe("wf-1");
        env.JobCount().ShouldBe(1);
    }

    [Test]
    public void SuccessfulScheduledPublish_UsesTheAbsoluteTimeOfTheRecord()
    {
        using var env = new PublicationEnv();
        var enqueuer = EnqueuerWith(env);

        var id = enqueuer.Schedule<HealthCheckJob>(j => j.Ping(null), FullPayload(), TimeSpan.FromHours(1));

        var state = env.StateData(id)!;
        state.Name.ShouldBe("Scheduled");
        var at = JobHelper.DeserializeDateTime(state.Data["EnqueueAt"]);
        var row = env.Row(id)!;
        at.ShouldBe(row.CreatedAtUtc.AddHours(1), "due time = original creation + delay, computed once");
        env.InSet(id).ShouldBeFalse();
    }

    [Test]
    public void NegativeDelay_SchedulesPastDue()
    {
        using var env = new PublicationEnv();
        var enqueuer = EnqueuerWith(env);

        var id = enqueuer.Schedule<HealthCheckJob>(j => j.Ping(null), FullPayload(), TimeSpan.FromMinutes(-5));

        env.State(id).ShouldBe("Scheduled");
        JobHelper.DeserializeDateTime(env.StateData(id)!.Data["EnqueueAt"]).ShouldBeLessThan(DateTime.UtcNow);
    }

    [Test]
    public void ParameterWriteFailureAfterCommit_ReturnsId_DoesNotCancel_KeepsIndexingAndMember()
    {
        using var env = new PublicationEnv();
        var faulty = new FaultyStorage(env.Storage)
        {
            ConnectionHook = (m, _) => m.Name == "SetJobParameter"
                ? throw new InvalidOperationException("params unavailable")
                : (false, null)
        };
        var client = new Mock<IBackgroundJobClient> { CallBase = false };
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns<Job, IState>((j, s) => env.Client.Create(j, s));
        var enqueuer = EnqueuerWith(env, client: client.Object, storage: faulty);

        var id = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload());

        id.ShouldNotBeNullOrEmpty();
        env.State(id).ShouldBe("Indexing");
        env.InSet(id).ShouldBeTrue();
        client.Verify(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
        env.StateHistoryCount(id, "Deleted").ShouldBe(0);
    }

    [Test]
    public void TransitionThrowsAfterCommit_ReturnsId_DoesNotCancel_KeepsIndexingAndMember()
    {
        using var env = new PublicationEnv();
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns<Job, IState>((j, s) => env.Client.Create(j, s));
        client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("lock timeout"));
        var enqueuer = EnqueuerWith(env, client: client.Object);

        var id = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload());

        env.State(id).ShouldBe("Indexing");
        env.InSet(id).ShouldBeTrue();
        client.Verify(x => x.ChangeState(id, It.IsAny<DeletedState>(), It.IsAny<string>()), Times.Never);
        env.Row(id).ShouldNotBeNull("JobIndex was prepared before the failing transition");
    }

    [Test]
    public async Task JobStaysRunnableAfterFailure_RecoveryPublishesTheSameJob()
    {
        using var env = new PublicationEnv();
        var enqueuer = EnqueuerWith(env, FailingPrepareWriter());
        var id = enqueuer.Enqueue<HealthCheckJob>(j => j.Ping(null), FullPayload());
        env.State(id).ShouldBe("Indexing");

        env.Time.Set(FakeTime.CeilMinute(DateTime.UtcNow.AddMinutes(10)));
        var summary = await env.NewService().RunPassAsync(env.Storage, env.Client, default);

        summary.Published.ShouldBe(1);
        env.State(id).ShouldBe("Enqueued");
        env.JobCount().ShouldBe(1);
        env.StateHistoryCount(id, "Deleted").ShouldBe(0);
    }

    [Test]
    public void PublishAsync_RejectedTransition_ForPublishedJob_ReportsAlreadyLeftIndexing_AndTouchesNothing()
    {
        using var env = new PublicationEnv();
        var record = IndexedJobPublicationRecord.ForImmediate(FullPayload(), DateTime.UtcNow, "pq");
        var id = env.CreateIndexing(record);
        env.Client.ChangeState(id, new EnqueuedState("pq"), "Indexing").ShouldBeTrue();
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(x => x.PrepareForPublicationAsync(id, It.IsAny<IndexPayload>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), default))
            .ReturnsAsync(JobIndexPreparationResult.Compatible);

        var result = IndexedJobPublication.PublishAsync(id, record, env.Storage, env.Client, writer.Object, "m",
            Mock.Of<ILogger>()).GetAwaiter().GetResult();

        result.Outcome.ShouldBe(IndexedJobPublicationOutcome.AlreadyLeftIndexing);
        result.ActualState.ShouldBe("Enqueued");
        env.StateHistoryCount(id, "Enqueued").ShouldBe(1);
    }

    [Test]
    public void PublishAsync_IncompatibleRow_DoesNotTransition()
    {
        using var env = new PublicationEnv();
        var record = IndexedJobPublicationRecord.ForImmediate(FullPayload(), DateTime.UtcNow, "pq");
        var id = env.CreateIndexing(record);
        var writer = new Mock<IJobIndexWriter>();
        writer.Setup(x => x.PrepareForPublicationAsync(id, It.IsAny<IndexPayload>(), It.IsAny<DateTime>(),
                It.IsAny<string>(), default))
            .ReturnsAsync(JobIndexPreparationResult.Incompatible);

        var result = IndexedJobPublication.PublishAsync(id, record, env.Storage, env.Client, writer.Object, "m",
            Mock.Of<ILogger>()).GetAwaiter().GetResult();

        result.Outcome.ShouldBe(IndexedJobPublicationOutcome.Incompatible);
        env.State(id).ShouldBe("Indexing");
    }

    [Test]
    public void PublishAsync_RejectedWhileStillIndexing_ReportsStillIndexing()
    {
        using var env = new PublicationEnv();
        var record = IndexedJobPublicationRecord.ForImmediate(FullPayload(), DateTime.UtcNow, "pq");
        var id = env.CreateIndexing(record);
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(false);

        var result = IndexedJobPublication.PublishAsync(id, record, env.Storage, client.Object, env.Writer, "m",
            Mock.Of<ILogger>()).GetAwaiter().GetResult();

        result.Outcome.ShouldBe(IndexedJobPublicationOutcome.StillIndexing);
    }

    [Test]
    public void PublishAsync_BlankOptionalParams_AreSkipped_ExactlyAsBefore()
    {
        using var env = new PublicationEnv();
        var record = IndexedJobPublicationRecord.ForImmediate(new IndexPayload("o", "d", " ", null, Path: ""), DateTime.UtcNow, "default");
        var id = env.CreateIndexing(record);

        IndexedJobPublication.PublishAsync(id, record, env.Storage, env.Client, env.Writer, "m", Mock.Of<ILogger>())
            .GetAwaiter().GetResult().Outcome.ShouldBe(IndexedJobPublicationOutcome.Published);

        env.Param(id, JobParamKeys.OrgSlug).ShouldBe("o");
        env.Param(id, JobParamKeys.DsSlug).ShouldBe("d");
        env.Param(id, JobParamKeys.Hash).ShouldBeNull();
        env.Param(id, JobParamKeys.Path).ShouldBeNull();
        env.Param(id, JobParamKeys.UserId).ShouldBeNull();
        env.Param(id, JobParamKeys.Queue).ShouldBeNull();
    }
}
