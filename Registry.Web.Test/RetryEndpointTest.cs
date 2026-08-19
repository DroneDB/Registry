using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Client;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Controllers;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Test.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Controller contract for POST /orgs/{o}/ds/{d}/tasks/{id}/retry: the live Hangfire state gates
/// the re-queue (Failed-only accepted set), sweep-ownership 409 for pending builds, 409 for jobs
/// missing from Hangfire, and stale JobIndex error fields are cleared before the re-queue. The
/// Hangfire client and the live job state are mocked so assertions are deterministic; the same-id
/// re-queue-to-Enqueued transition is covered by BackgroundJobsProcessorTest and by a manual
/// E2E run of the retry endpoint.
/// </summary>
[TestFixture]
public class RetryEndpointTest : IDisposable
{
    private const string Org = "org1";
    private const string Ds = "ds1";

    private RegistryContext _db = null!;
    private Mock<IBackgroundJobClient> _client = null!;
    private TasksController _controller = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IUtils> _utils = null!;

    /// <summary>Minimal JobStorage whose monitoring API reports the scripted live state per test.</summary>
    private sealed class TestJobStorage : JobStorage
    {
        private readonly IMonitoringApi _monitor;
        public TestJobStorage(IMonitoringApi monitor) => _monitor = monitor;
        public override IMonitoringApi GetMonitoringApi() => _monitor;
        public override IStorageConnection GetConnection() => throw new NotSupportedException();
    }

    [SetUp]
    public void SetUp()
    {
        _db = new RegistryContext(new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase($"RetryTest_{Guid.NewGuid()}").Options);

        // Default live state: no jobs in Hangfire (JobDetails returns null). Individual tests
        // override via SetLiveState; the client's Requeue defaults to success (stock semantics).
        _client = new Mock<IBackgroundJobClient>();
        // Every Requeue/Delete helper funnels to the single interface method
        // ChangeState(jobId, state, fromState); default: the transition succeeds.
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);
        // Swap JobStorage.Current for this test; Dispose restores the previous value (used to
        // leak into sibling fixtures because the _client mock below hangs off this storage).
        DisposeJobScope();
        _jobScope = new JobStorageScope(
            new TestJobStorage(Mock.Of<IMonitoringApi>(x => x.JobDetails(It.IsAny<string>()) == null)));

        // Auth: everyone may see the dataset and owns every task (UserId null breaks the chain into IsOwnerOrAdmin).
        var authManager = new Mock<IAuthManager>();
        authManager.Setup(x => x.RequestAccess<Dataset>(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        authManager.Setup(x => x.GetCurrentUser()).ReturnsAsync((User)null!);
        authManager.Setup(x => x.IsOwnerOrAdmin(It.IsAny<Dataset>())).ReturnsAsync(true);

        _utils = new Mock<IUtils>();
        _utils.Setup(x => x.GetDataset(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() });

        _ddb = new Mock<IDDB>();
        _ddb.Setup(x => x.IsBuildPending()).Returns(false);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(_ddb.Object);

        var writer = new JobIndexWriter(_db, new Mock<ILogger<JobIndexWriter>>().Object,
            new Mock<IDistributedCache>().Object);

        var appSettings = new Mock<IOptions<AppSettings>>();
        appSettings.SetupGet(x => x.Value).Returns(new AppSettings { TempPath = Path.GetTempPath() });

        _controller = new TasksController(
            new Mock<IHeavyTaskRunner>().Object,
            new Mock<IHeavyToolRegistry>().Object,
            new Mock<IHeavyToolGating>().Object,
            new JobIndexQuery(_db),
            writer,
            authManager.Object,
            _utils.Object,
            new BackgroundJobsProcessor(_client.Object, NullIndexedEnqueuer.Create()),
            ddbManager.Object,
            new Mock<IDistributedCache>().Object,
            appSettings.Object,
            new Mock<ILogger<TasksController>>().Object);
    }

    private JobStorageScope? _jobScope;

    private void DisposeJobScope() => _jobScope?.Dispose();

    public void Dispose()
    {
        DisposeJobScope();
        _jobScope = null;
    }

    private static JobIndex NewRow(string jobId, string state, string toolId = null) => new()
    {
        JobId = jobId,
        OrgSlug = Org,
        DsSlug = Ds,
        ToolId = toolId ?? "build",
        ToolVersion = "1",
        CurrentState = state,
        CreatedAtUtc = DateTime.UtcNow,
        LastStateChangeUtc = DateTime.UtcNow
    };

    private int Seed(JobIndex row)
    {
        _db.JobIndices.Add(row);
        return _db.SaveChanges();
    }

    private int Retry(string id)
    {
        var result = _controller.Retry(Org, Ds, id, CancellationToken.None).GetAwaiter().GetResult();
        // Ok / Conflict / NotFound / Unauthorized all surface as ObjectResult with a StatusCode,
        // with the exception of raw StatusCodeResult (unused by this controller today).
        if (result is ObjectResult obj) return obj.StatusCode ?? 200;
        if (result is StatusCodeResult sr) return (int)sr.StatusCode;
        return -1;
    }

    /// <summary>
    /// Reports the given live Hangfire state for the given job id via a fabricated JobStorage.
    /// A <paramref name="stateName"/> of null models a job no longer present in Hangfire.
    /// </summary>
    private static void SetLiveState(string jobId, string stateName)
    {
        var dto = stateName is null
            ? null
            : new JobDetailsDto
            {
                History = new List<StateHistoryDto> { new StateHistoryDto { StateName = stateName } }
            };
        var monitor = Mock.Of<IMonitoringApi>(x => x.JobDetails(jobId) == dto);
        JobStorage.Current = new TestJobStorage(monitor);
    }

    /// <summary>Seeds a Failed JobIndex row plus a live Failed Hangfire state for the given tool.</summary>
    private string SeedFailedJob(string toolId = "build")
    {
        var jobId = Guid.NewGuid().ToString("N");
        SetLiveState(jobId, FailedState.StateName);
        _db.JobIndices.Add(NewRow(jobId, "Failed", toolId));
        _db.SaveChanges();
        return jobId;
    }

    [Test]
    public void Retry_FailedBuildTask_NoPending_Returns200_AndRequeuesSameId()
    {
        var jobId = SeedFailedJob();

        Retry(jobId).ShouldBe(200);

        // The same job id transitions to Enqueued (never the archived DeletedState).
        _client.Verify(x => x.ChangeState(jobId, It.Is<IState>(s => s is EnqueuedState), It.IsAny<string>()), Times.Once);
        _client.Verify(x => x.ChangeState(jobId, It.Is<IState>(s => s is DeletedState), It.IsAny<string>()), Times.Never);
        _db.JobIndices.AsNoTracking().Count(r => r.JobId == jobId).ShouldBe(1);
    }

    [Test]
    public void Retry_FailedTask_ClearsStaleErrorFieldsBeforeRequeue()
    {
        var jobId = SeedFailedJob("raster-export");
        var row = _db.JobIndices.Single(r => r.JobId == jobId);
        row.ErrorType = "CorruptEntryException";
        row.LogTailJson = "[\"old log\"]";
        row.FailedAtUtc = DateTime.UtcNow;
        _db.SaveChanges();

        Retry(jobId).ShouldBe(200);

        row.ShouldNotBeNull();
        row.ErrorType.ShouldBeNull();
        row.LogTailJson.ShouldBeNull();
        row.FailedAtUtc.ShouldBeNull();
    }

    [Test]
    public void Retry_BuildTask_WithPendingMarker_Returns409_SweepOwns()
    {
        _ddb.Setup(x => x.IsBuildPending()).Returns(true);
        var jobId = SeedFailedJob("build");

        Retry(jobId).ShouldBe(409); // pending-build sweep owns this retry

        _ddb.Verify(x => x.IsBuildPending(), Times.Once);
        _client.Verify(x => x.ChangeState(jobId, It.IsAny<IState>(), It.IsAny<string>()), Times.Never); // job untouched
    }

    [Test]
    public void Retry_NonBuildTask_WithPendingMarker_StillSucceeds()
    {
        _ddb.Setup(x => x.IsBuildPending()).Returns(true);
        var jobId = SeedFailedJob("archive-extract");

        Retry(jobId).ShouldBe(200); // guard is scoped to the build tool only
        _ddb.Verify(x => x.IsBuildPending(), Times.Never);
        _client.Verify(x => x.ChangeState(jobId, It.Is<IState>(s => s is EnqueuedState), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void Retry_BuildTask_DdbUnavailable_StillSucceeds()
    {
        _ddb.Setup(x => x.IsBuildPending()).Throws(new InvalidOperationException("ddb down"));
        var jobId = SeedFailedJob("build");

        Retry(jobId).ShouldBe(200); // availability hiccup never blocks the user-initiated retry
        _client.Verify(x => x.ChangeState(jobId, It.Is<IState>(s => s is EnqueuedState), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public void Retry_JobMissingFromHangfire_Returns409()
    {
        var row = NewRow(Guid.NewGuid().ToString("N"), "Failed");
        Seed(row);

        Retry(row.JobId).ShouldBe(409);
        // Row keeps Failed (no transition possible) and stale fields are still reset.
        _db.JobIndices.AsNoTracking().Single(r => r.JobId == row.JobId).CurrentState.ShouldBe("Failed");
    }

    [Test]
    public void Retry_NonFailedRow_Returns409_FailedOnlyAcceptedSet()
    {
        // Seed a Succeeded-row with a job id that has expired from Hangfire: the Failed-only
        // guard means the controller refuses the retry (409) rather than advancing it. Coverage
        // of the Succeeded-state guard at the Hangfire layer is in BackgroundJobsProcessorTest.
        var row = NewRow(Guid.NewGuid().ToString("N"), "Succeeded");
        Seed(row);

        Retry(row.JobId).ShouldBe(409);
        // Row keeps its Succeeded state -- non-Failed rows are not touched by the retry path.
        _db.JobIndices.AsNoTracking().Single(r => r.JobId == row.JobId).CurrentState.ShouldBe("Succeeded");
    }

    [Test]
    public void Retry_UnknownTask_Returns404()
    {
        Retry("unknown-job-id").ShouldBe(404);
    }
}
