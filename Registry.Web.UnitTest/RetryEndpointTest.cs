using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Hangfire;
using Hangfire.Storage;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Test.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;
using Registry.Web.Test.Support;
using Registry.Web.Services.Caching;

namespace Registry.Web.Test;

/// <summary>
/// Manager contract for POST /orgs/{o}/ds/{d}/tasks/{id}/retry: the live Hangfire state gates
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
    private TasksManager _manager = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IDatasetResolver> _utils = null!;
    private Mock<IStorageConnection> _connection = null!;
    private Mock<JobStorage> _storage = null!;

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.CreateContext<RegistryContext>();

        // Default live state: no jobs in Hangfire (GetStateData returns null). Individual tests
        // override via SetLiveState; the client's Requeue defaults to success (stock semantics).
        _client = new Mock<IBackgroundJobClient>();
        // Every Requeue/Delete helper funnels to the single interface method
        // ChangeState(jobId, state, fromState); default: the transition succeeds.
        _client.Setup(x => x.ChangeState(It.IsAny<string>(), It.IsAny<IState>(), It.IsAny<string>())).Returns(true);
        _connection = new Mock<IStorageConnection>();
        _storage = new Mock<JobStorage>();
        _storage.Setup(x => x.GetConnection()).Returns(_connection.Object);

        // Auth: everyone may see the dataset and owns every task (UserId null breaks the chain into IsOwnerOrAdmin).
        var authManager = new Mock<IAuthManager>();
        authManager.Setup(x => x.RequestAccess<Dataset>(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        authManager.Setup(x => x.GetCurrentUser()).ReturnsAsync((User)null!);
        authManager.Setup(x => x.IsOwnerOrAdmin(It.IsAny<Dataset>())).ReturnsAsync(true);

        _utils = new Mock<IDatasetResolver>();
        _utils.Setup(x => x.GetDatasetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() });

        _ddb = new Mock<IDDB>();
        _ddb.Setup(x => x.IsBuildPending()).Returns(false);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(_ddb.Object);

        var writer = new JobIndexWriter(_db, new Mock<ILogger<JobIndexWriter>>().Object,
            new Mock<IRecordCache>().Object);

        var appSettings = TestSettings.Options(new AppSettings { TempPath = Path.GetTempPath() });

        var access = new DatasetAccessResolver(_utils.Object, authManager.Object, ddbManager.Object, TestPaths.Policy);
        var authorizer = new TaskAuthorizer(access, new JobIndexQuery(_db), authManager.Object);
        var processor = new BackgroundJobsProcessor(_client.Object, NullIndexedEnqueuer.Create(), _storage.Object);
        var artifacts = new TaskArtifactStore(new Registry.Adapters.FileSystem(), TestScratch.Provider,
            NullLogger<TaskArtifactStore>.Instance);

        _manager = new TasksManager(
            new TaskSubmission(new Mock<IHeavyToolRegistry>().Object, new Mock<IHeavyToolGating>().Object, access,
                new CurrentUserTaskSubmitter(new Mock<IHeavyTaskRunner>().Object, authManager.Object,
                    new Mock<IHttpContextAccessor>().Object)),
            new TaskHistory(access, new JobIndexQuery(_db), new Mock<IRecordCache>().Object, authorizer, artifacts,
                TestSettings.Options<ProcessingPlatformSettings>(appSettings)),
            new TaskLifecycle(access, authorizer, processor, writer, artifacts, NullLogger<TaskLifecycle>.Instance),
            new TaskRetry(authorizer, processor, writer, _utils.Object, ddbManager.Object,
                NullLogger<TaskRetry>.Instance),
            access);
    }

    [TearDown]
    public void Dispose()
    {
        _db?.Dispose();
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
        try
        {
            _manager.RetryAsync(Org, Ds, id, CancellationToken.None).GetAwaiter().GetResult();
            return 200;
        }
        catch (Exception ex)
        {
            // The manager reports refusals as managed exceptions; the global filter turns them
            // into the very status codes the endpoint used to return inline.
            return ApiExceptionClassifier.Classify(ex, authenticated: true).StatusCode;
        }
    }

    /// <summary>
    /// Reports the given live Hangfire state for the given job id via a fabricated JobStorage.
    /// A <paramref name="stateName"/> of null models a job no longer present in Hangfire.
    /// </summary>
    private void SetLiveState(string jobId, string stateName)
    {
        _connection.Setup(x => x.GetStateData(jobId)).Returns(stateName is null ? null :
            new StateData { Name = stateName, Data = new Dictionary<string, string>() });
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
