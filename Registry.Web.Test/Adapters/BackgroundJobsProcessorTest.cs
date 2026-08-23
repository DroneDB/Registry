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
/// terminal operation. The Hangfire client and the live job state (read from the monitoring
/// storage) are mocked so the assertions are deterministic and never depend on an in-process
/// Hangfire server being able to process a job.
/// </summary>
[TestFixture]
public class BackgroundJobsProcessorTest
{
    private readonly Mock<IBackgroundJobClient> _client;
    private readonly BackgroundJobsProcessor _processor;

    /// <summary>Minimal JobStorage whose monitoring API reports a scripted live state per job id.</summary>
    private sealed class TestJobStorage : JobStorage
    {
        private readonly IMonitoringApi _monitor;

        public TestJobStorage(IMonitoringApi monitor) => _monitor = monitor;
        public override IMonitoringApi GetMonitoringApi() => _monitor;
        public override IStorageConnection GetConnection() => throw new NotSupportedException();
    }

    private JobStorageScope? _jobStorageScope;

    public BackgroundJobsProcessorTest()
    {
        _client = new Mock<IBackgroundJobClient>();
        _processor = new BackgroundJobsProcessor(_client.Object, NullIndexedEnqueuer.Create());

        // Baseline storage for this test instance (missing job => JobDetails yields null).
        // Passed-reset isolation: fixtures no longer rely on another fixture having
        // leaked its TestJobStorage into the static JobStorage.Current (see JobStorageScope).
        _jobStorageScope = new JobStorageScope(new TestJobStorage(
            Mock.Of<IMonitoringApi>(m => m.JobDetails(It.IsAny<string>()) == null)));
    }

    [TearDown]
    public void TearDown()
    {
        // Restore JobStorage.Current (used to leak the fixture's TestJobStorage into other
        // fixtures because nothing ever cleared JobStorage.Current).
        _jobStorageScope?.Dispose();
        _jobStorageScope = null;
    }

    /// <summary>
    /// Points the static <c>JobStorage.Current</c> at a storage whose monitoring API reports the
    /// given live state for the given job id (the same read the Failed-only requeue guard uses).
    /// A <paramref name="stateName"/> of null models a job no longer present in Hangfire
    /// (<c>JobDetails</c> returns null).
    /// </summary>
    private void SetLiveState(string jobId, string stateName)
    {
        var dto = stateName is null
            ? null
            : new JobDetailsDto
            {
                History = new List<StateHistoryDto> { new StateHistoryDto { StateName = stateName } }
            };
        var monitor = Mock.Of<IMonitoringApi>(x => x.JobDetails(jobId) == dto);
        // Swap per call scope; dispose the previous replacement so the restore chain reaches
        // the original (null) value on teardown.
        _jobStorageScope?.Dispose();
        _jobStorageScope = new JobStorageScope(new TestJobStorage(monitor));
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
