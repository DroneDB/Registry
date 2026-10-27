#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Filters;
using Registry.Web.Hosting;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// Which ends of build jobs are reported, through the real filter and classifier with a hand-built state change and
/// libddb's answers mocked (the build state of the entry, its pending markers): one test per case of the rules, and the
/// guarantee that a notification failure never fails the state change.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildNotificationFilterTests"
/// </summary>
[TestFixture]
public class BuildNotificationFilterTests
{
    private const string JobId = "build-job";
    private const string Org = "org";
    private const string Ds = "ds";
    private const string Path = "cloud.laz";
    private const string Hash = "cloud-hash";
    private const string User = "jane";
    private const string Folder = "/data/datasets/org/ds";

    private RegistryContext _context = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private Mock<IBuildArtifactResolver> _builds = null!;
    private Mock<IEmailService> _email = null!;
    private Mock<IBuildNotificationRecorder> _recorder = null!;
    private List<BuildReport> _reports = null!;
    private ServiceProvider _provider = null!;
    private BuildNotificationFilter _filter = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestDb.CreateContext<RegistryContext>(db =>
        {
            var organization = new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow };
            organization.Datasets = [new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow }];
            db.Organizations.Add(organization);
            db.SaveChanges();
        });

        _ddb = new Mock<IDDB>();
        _ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([]);
        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Open(Org, It.IsAny<Guid>())).Returns(_ddb.Object);
        _builds = new Mock<IBuildArtifactResolver>();
        Entry(BuildState.Complete);

        _email = new Mock<IEmailService>();
        _email.SetupGet(e => e.IsEnabled).Returns(true);
        _reports = [];
        _recorder = new Mock<IBuildNotificationRecorder>();
        _recorder.Setup(r => r.RecordAsync(It.IsAny<BuildReport>(), It.IsAny<CancellationToken>()))
            .Callback<BuildReport, CancellationToken>((report, _) => _reports.Add(report))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_context);
        services.AddSingleton(_email.Object);
        services.AddSingleton(_ddbManager.Object);
        services.AddSingleton(_builds.Object);
        services.AddSingleton(_recorder.Object);
        services.AddSingleton<DatasetSweep>();
        services.AddSingleton<IBuildOutcomeClassifier, BuildOutcomeClassifier>();
        services.AddScoped<FinishedBuildReporter>();
        _provider = services.BuildServiceProvider();
        _filter = new BuildNotificationFilter(_provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BuildNotificationFilter>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _context.Dispose();
    }

    private void Entry(BuildState state, string hash = Hash) =>
        _builds.Setup(b => b.GetBuild(_ddb.Object, Path))
            .Returns(TestBuilds.Build(Folder, Path, EntryType.PointCloud, hash, state));

    private static Job BuildJob() => Job.FromExpression<DdbJobs>(j => j.Build(Org, Guid.Empty, Path, false, null));

    private void Apply(Job job, IState state, string? userId = User, string? hash = Hash, string? path = null)
    {
        var connection = new Mock<JobStorageConnection>();
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.OrgSlug)).Returns(Org);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.DsSlug)).Returns(Ds);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.Hash)).Returns(hash!);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.UserId)).Returns(userId!);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.Path)).Returns(path!);
        var context = new ApplyStateContext(new InMemoryStorage(), connection.Object, Mock.Of<IWriteOnlyTransaction>(),
            new BackgroundJob(JobId, job, DateTime.UtcNow), state, ProcessingState.StateName);

        _filter.OnStateApplied(context, Mock.Of<IWriteOnlyTransaction>());
    }

    private static FailedState Failed(string message = "cannot read the file") =>
        new(new DdbException($"{message} {Folder}/{Path}"));

    // --- Succeeded ---

    [Test]
    public void EntryBuild_SucceededAndBuilt_IsCompleted()
    {
        Apply(BuildJob(), new SucceededState(null, 0, 0));

        var report = _reports.ShouldHaveSingleItem();
        report.ShouldBe(new BuildReport(Org, Ds, Path, Hash, BuildOutcome.Completed, null, new BuildRequest(User, JobId)));
    }

    [Test]
    public void BackgroundRebuild_SucceededAndBuilt_IsCompleted()
    {
        Apply(Job.FromExpression<DdbJobs>(j => j.RebuildInBackground(Org, Guid.Empty, Path, null)),
            new SucceededState(null, 0, 0), userId: MagicStrings.AutoBuildServiceUserId);

        _reports.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.Completed);
    }

    [Test]
    public void BuildOfTheShapeOfEarlierReleases_SucceededAndBuilt_IsCompleted()
    {
        var method = typeof(HangfireUtils).GetMethod(nameof(HangfireUtils.BuildWrapper))!;
        Apply(new Job(typeof(HangfireUtils), method, null, Path, false, null), new SucceededState(null, 0, 0));

        _reports.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.Completed);
    }

    [Test]
    public void BuildTask_SucceededAndBuilt_IsCompleted()
    {
        Apply(Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(HeavyToolIds.Build, "1", "{}", null!, CancellationToken.None)),
            new SucceededState(null, 0, 0), hash: null, path: Path);

        // The task carries no hash: the entry's, from libddb
        _reports.ShouldHaveSingleItem().ShouldBe(new BuildReport(Org, Ds, Path, Hash, BuildOutcome.Completed, null,
            new BuildRequest(User, JobId)));
    }

    [TestCase(BuildState.Pending, TestName = "Succeeded with a missing dependency (pending) is not final")]
    [TestCase(BuildState.Building, TestName = "Succeeded because another process held the lock is not final")]
    [TestCase(BuildState.None, TestName = "Succeeded without a complete build is not final")]
    public void EntryBuild_SucceededButNotBuilt_IsNotReported(BuildState state)
    {
        Entry(state);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
    }

    // --- Failed ---

    [Test]
    public void EntryBuild_FailedWithoutPendingMarker_IsFailed_WithTheErrorScrubbed()
    {
        Entry(BuildState.None);

        Apply(BuildJob(), Failed());

        var report = _reports.ShouldHaveSingleItem();
        report.Outcome.ShouldBe(BuildOutcome.Failed);
        report.Error.ShouldBe($"DdbException: cannot read the file {JobLogText.DatasetPlaceholder}/{Path}");
        report.Request.ShouldBe(new BuildRequest(User, JobId));
    }

    [Test]
    public void EntryBuild_FailedWithItsPendingMarker_IsNotFinal_TheRetriesDecide()
    {
        Entry(BuildState.None);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([PendingMarkers.Of(Path, Hash)]);

        Apply(BuildJob(), Failed("disk nearly full"));

        _reports.ShouldBeEmpty();
    }

    [TestCase("other.laz", Hash)]
    [TestCase(Path, "other-hash")]
    public void EntryBuild_FailedWithTheMarkerOfAnotherEntry_IsFailed(string markerPath, string markerHash)
    {
        Entry(BuildState.None);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([PendingMarkers.Of(markerPath, markerHash)]);

        Apply(BuildJob(), Failed());

        _reports.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.Failed);
    }

    [TestCase(BuildState.Complete, TestName = "Failed but built meanwhile by another job is not reported")]
    [TestCase(BuildState.Building, TestName = "Failed while another job builds the entry is not reported")]
    public void EntryBuild_FailedWhileAnotherJobBuildsIt_IsNotReported(BuildState state)
    {
        Entry(state);

        Apply(BuildJob(), Failed());

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void BuildTask_FailedWithoutPendingMarker_IsFailed()
    {
        Entry(BuildState.None);

        Apply(Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(HeavyToolIds.Build, "1", "{}", null!, CancellationToken.None)),
            Failed(), path: Path);

        _reports.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.Failed);
    }

    // --- Not an entry build ---

    [Test]
    public void WholeDatasetBuild_IsNotReported()
    {
        Apply(Job.FromExpression<DdbJobs>(j => j.Build(Org, Guid.Empty, null, false, null)), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
        _ddbManager.Verify(m => m.Open(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public void PendingBuildCoordinator_IsNotReported()
    {
        Apply(Job.FromExpression<DdbJobs>(j => j.BuildPending(Org, Guid.Empty, null)), Failed());

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void Cleanup_IsNotReported()
    {
        Apply(Job.FromExpression<DdbJobs>(j => j.Cleanup(Org, Guid.Empty, null)), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void TaskOfAnotherTool_IsNotReported()
    {
        Apply(Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(HeavyToolIds.RescanIndex, "1", "{}", null!, CancellationToken.None)),
            new SucceededState(null, 0, 0), path: Path);

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void BuildTaskOfPendingEntries_WithoutPath_IsNotReported()
    {
        Apply(Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(HeavyToolIds.Build, "1", "{}", null!, CancellationToken.None)),
            new SucceededState(null, 0, 0), path: null);

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void AnAttemptHangfireRetries_IsNotReported()
    {
        // The retry policy turns the failure of an attempt into Scheduled: the job is not over
        Apply(BuildJob(), new ScheduledState(TimeSpan.FromMinutes(1)));

        _reports.ShouldBeEmpty();
    }

    // --- Guards ---

    [Test]
    public void EmailOff_NothingIsAskedOfLibddb()
    {
        _email.SetupGet(e => e.IsEnabled).Returns(false);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
        _builds.Verify(b => b.GetBuild(It.IsAny<IDDB>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void DatasetDeletedSince_IsNotReported()
    {
        _ddbManager.Setup(m => m.Open(Org, It.IsAny<Guid>())).Throws(new DirectoryNotFoundException("gone"));

        Should.NotThrow(() => Apply(BuildJob(), new SucceededState(null, 0, 0)));

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void ANotificationFailure_NeverFailsTheStateChange()
    {
        _recorder.Setup(r => r.RecordAsync(It.IsAny<BuildReport>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        Should.NotThrow(() => Apply(BuildJob(), new SucceededState(null, 0, 0)));
    }

    [Test]
    public void TheFilter_RunsAfterTheJobIndexStateIsWritten()
    {
        // Who asked is read from the job index: its row must hold the final state when this filter runs
        var order = RegistryJobsServiceCollectionExtensions.JobFilterTypes.ToList();

        order.IndexOf(typeof(BuildNotificationFilter)).ShouldBeGreaterThan(order.IndexOf(typeof(JobIndexStateFilter)));
        order.IndexOf(typeof(BuildRetrySchedulerFilter)).ShouldBeGreaterThan(order.IndexOf(typeof(JobIndexStateFilter)));
    }
}
