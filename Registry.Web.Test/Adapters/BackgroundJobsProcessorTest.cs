using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Client;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Hangfire.States;
using Moq;
using NUnit.Framework;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test.Adapters;

/// <summary>
/// Requeue contract for <see cref="BackgroundJobsProcessor"/>: requeue must delegate to the
/// client's same-id re-queue and be permitted from Failed only; delete must remain a separate
/// terminal operation. The Hangfire client and the live job state (read from the configured
/// storage) are mocked so the assertions are deterministic and never depend on an in-process
/// Hangfire server being able to process a job.
/// </summary>
[TestFixture]
public class BackgroundJobsProcessorTest
{
    private Mock<IBackgroundJobClient> _client;
    private Mock<IStorageConnection> _connection;
    private Mock<JobStorage> _storage;
    private BackgroundJobsProcessor _processor;

    [SetUp]
    public void SetUp()
    {
        _client = new Mock<IBackgroundJobClient>();
        _connection = new Mock<IStorageConnection>();
        _storage = new Mock<JobStorage>();
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);
        // Inject storage per test; these fixtures do not mutate global storage.
        _processor = new BackgroundJobsProcessor(_client.Object, NullIndexedEnqueuer.Create(), _storage.Object);
    }

    /// <summary>
    /// Reports the current stored state for the given job id.
    /// A <paramref name="stateName"/> of null models a job no longer present in Hangfire
    /// (<c>GetStateData</c> returns null).
    /// </summary>
    private void SetLiveState(string jobId, string stateName)
    {
        _connection.Setup(x => x.GetStateData(jobId)).Returns(stateName is null ? null :
            new StateData { Name = stateName, Data = new Dictionary<string, string>() });
    }

    [TestCase("Succeeded", "Failed", false)]
    [TestCase("Failed", "Processing", true)]
    public void Requeue_CurrentStoredStateWinsOverMonitoringHistory(string currentState, string historyState, bool accepted)
    {
        const string jobId = "history-out-of-order";
        SetLiveState(jobId, currentState);
        var history = new JobDetailsDto
        {
            History = new List<StateHistoryDto> { new() { StateName = historyState } }
        };
        _storage.Setup(x => x.GetMonitoringApi()).Returns(Mock.Of<IMonitoringApi>(x => x.JobDetails(jobId) == history));
        _client.Setup(x => x.ChangeState(jobId, It.IsAny<IState>(), FailedState.StateName)).Returns(true);

        _processor.Requeue(jobId).ShouldBe(accepted);
        _client.Verify(x => x.ChangeState(jobId, It.IsAny<EnqueuedState>(), FailedState.StateName),
            accepted ? Times.Once() : Times.Never());
    }

    [Test]
    public void GetJobStatus_CurrentStoredStateWinsOverMonitoringHistory()
    {
        const string jobId = "current-coordinator";
        SetLiveState(jobId, ScheduledState.StateName);
        var history = new JobDetailsDto
        {
            History = new List<StateHistoryDto> { new() { StateName = FailedState.StateName } }
        };
        _storage.Setup(x => x.GetMonitoringApi()).Returns(Mock.Of<IMonitoringApi>(x => x.JobDetails(jobId) == history));
        _processor.GetJobStatus(jobId).ShouldBe(JobStatus.Scheduled);
    }

    [Test]
    public void Requeue_FailedJob_ChangesTheSameIdToEnqueued_NotDeleted()
    {
        // Regression: the old body applied a DeletedState (terminal archive). Requeue must instead
        // apply an EnqueuedState to the SAME job id, transitioning FROM its current Failed state.
        const string jobId = "job-1";
        SetLiveState(jobId, FailedState.StateName);
        IState applied = null;
        string appliedFrom = null;
        _client.Setup(x => x.ChangeState(jobId, It.IsAny<IState>(), It.IsAny<string>()))
            .Callback<string, IState, string>((id, state, from) => { applied = state; appliedFrom = from; })
            .Returns(true);

        _processor.Requeue(jobId).ShouldBeTrue();

        applied.ShouldBeOfType<EnqueuedState>(); // Enqueued -- NOT the archived DeletedState
        appliedFrom.ShouldBe(FailedState.StateName); // a same-id, Failed-only (guarded) transition
    }

    [Test]
    public void Requeue_MissingJob_ReturnsFalse_AndMakesNoStateChange()
    {
        const string jobId = "no-such-job-id";
        SetLiveState(jobId, null);

        _processor.Requeue(jobId).ShouldBeFalse();

        _client.Verify(x => x.ChangeState(jobId, It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void Requeue_LiveStateNotFailed_ReturnsFalse_AndMakesNoStateChange()
    {
        const string jobId = "job-live";
        SetLiveState(jobId, ProcessingState.StateName);

        _processor.Requeue(jobId).ShouldBeFalse();

        _client.Verify(x => x.ChangeState(jobId, It.IsAny<IState>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void Requeue_ClientReportsFailure_ReturnsFalse()
    {
        const string jobId = "job-ce";
        SetLiveState(jobId, FailedState.StateName);
        _client.Setup(x => x.ChangeState(jobId, It.IsAny<IState>(), It.IsAny<string>())).Returns(false);

        _processor.Requeue(jobId).ShouldBeFalse();
    }

    [Test]
    public void Delete_ArchivesTheSameIdToDeleted_NotEnqueued()
    {
        const string jobId = "job-del";
        IState applied = null;
        _client.Setup(x => x.ChangeState(jobId, It.IsAny<IState>(), It.IsAny<string>()))
            .Callback<string, IState, string>((id, state, from) => applied = state)
            .Returns(true);

        _processor.Delete(jobId).ShouldBeTrue();

        applied.ShouldBeOfType<DeletedState>(); // Delete still archives: a separate, terminal operation
    }
}
