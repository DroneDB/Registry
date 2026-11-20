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
using Microsoft.EntityFrameworkCore;
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
using Registry.Web.Services.Builds;
using Registry.Web.Services.Caching;
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
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<BuildNotificationOutbox>();
        services.AddScoped<FinishedBuildReporter>();
        services.AddSingleton(Mock.Of<IRecordCache>());
        services.AddSingleton<IJobIndexQuery, JobIndexQuery>();
        services.AddSingleton<IJobIndexWriter>(p => new JobIndexWriter(_context,
            NullLogger<JobIndexWriter>.Instance, p.GetRequiredService<IRecordCache>()));
        services.AddScoped<FinishedBuildHandler>();
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

    private void Apply(Job job, IState state, string? userId = User, string? hash = Hash, string? path = null,
        string? oldState = null)
    {
        var connection = new Mock<JobStorageConnection>();
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.OrgSlug)).Returns(Org);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.DsSlug)).Returns(Ds);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.Hash)).Returns(hash!);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.UserId)).Returns(userId!);
        connection.Setup(c => c.GetJobParameter(JobId, JobParamKeys.Path)).Returns(path!);
        var context = new ApplyStateContext(new InMemoryStorage(), connection.Object, Mock.Of<IWriteOnlyTransaction>(),
            new BackgroundJob(JobId, job, DateTime.UtcNow), state, oldState ?? ProcessingState.StateName);

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

    [TestCase(BuildState.Pending, TestName = "Succeeded with the entry pending but no marker of its content is not reported")]
    [TestCase(BuildState.Building, TestName = "Succeeded because another process held the lock is not final")]
    [TestCase(BuildState.None, TestName = "Succeeded without a complete build is not final")]
    public void EntryBuild_SucceededButNotBuilt_IsNotReported(BuildState state)
    {
        Entry(state);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
    }

    // --- Succeeded without building: the build found files missing ---

    // libddb's marker of a build that found files missing (dataset-relative names)
    private static PendingBuildInfo Waiting(string path = Path, string hash = Hash, bool awaitingFiles = true)
    {
        var marker = PendingMarkers.Of(path, hash, 0, "cloud.txt", "colors/palette.json");
        marker.AwaitingFiles = awaitingFiles;
        return marker;
    }

    [Test]
    public void EntryBuild_SucceededLeavingItsContentWaitingForFiles_WaitsForThoseFiles()
    {
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        _reports.ShouldHaveSingleItem().ShouldBe(new BuildReport(Org, Ds, Path, Hash, BuildOutcome.WaitingForFiles,
            "cloud.txt, colors/palette.json", new BuildRequest(User, JobId)));
    }

    [Test]
    public void ACoordinatorBuild_SucceededLeavingItsContentWaitingForFiles_WaitsForThoseFiles()
    {
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);

        Apply(Job.FromExpression<DdbJobs>(j => j.RebuildInBackground(Org, Guid.Empty, Path, null)),
            new SucceededState(null, 0, 0), userId: MagicStrings.AutoBuildServiceUserId);

        _reports.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.WaitingForFiles);
    }

    [Test]
    public void EntryBuild_SucceededWithAMarkerAFileAlreadyReleased_IsNotReported()
    {
        // A missing file arrived after the build looked: the marker is due, the build runs again at once
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting(awaitingFiles: false)]);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
    }

    [TestCase("other.laz", Hash)]
    [TestCase(Path, "other-hash")]
    public void EntryBuild_SucceededWithTheWaitOfAnotherEntry_IsNotReported(string markerPath, string markerHash)
    {
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting(markerPath, markerHash)]);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void EntryBuild_SucceededWhileAnotherProcessBuildsIt_IsNotReported_EvenWithAWait()
    {
        Entry(BuildState.Building);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        _reports.ShouldBeEmpty();
    }

    [Test]
    public void EntryBuild_FailedWithAWaitingMarker_IsNotFinal()
    {
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);

        Apply(BuildJob(), Failed());

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
        report.Error.ShouldBe($"cannot read the file {JobLogText.DatasetPlaceholder}/{Path}");
        report.Request.ShouldBe(new BuildRequest(User, JobId));
    }

    [Test]
    public void EntryBuild_FailedByAWrappedNativeError_TellsTheNativeCauseOnly()
    {
        Entry(BuildState.None);

        // The build adapter wraps the native error; the user is told what is wrong with the file, not the wrapper.
        Apply(BuildJob(), new FailedState(new InvalidOperationException($"Cannot build '{Path}' from ddb '{Folder}'",
            new DdbException("COPC build failed: readers.las: Invalid version 989819808 found in LAZ chunk table."))));

        _reports.ShouldHaveSingleItem().Error
            .ShouldBe("COPC build failed: readers.las: Invalid version 989819808 found in LAZ chunk table.");
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

    // --- A user's retry of the same job ---

    private BuildNotificationOutbox Outbox() => new(_context, TimeProvider.System);

    private static BuildReport FailedReport() =>
        new(Org, Ds, Path, Hash, BuildOutcome.Failed, "it broke", new BuildRequest(User, JobId));

    private static readonly BuildRequester Requester = new(User, JobId);

    // The failure of the job is told and sent: its request is answered
    private async Task FailureSent()
    {
        var outbox = Outbox();
        (await outbox.AddAsync(FailedReport(), Requester, CancellationToken.None)).ShouldNotBeNull();
        (await outbox.ClaimAsync(User, "digest", CancellationToken.None)).ShouldHaveSingleItem();
        await outbox.MarkSentAsync("digest", CancellationToken.None);
        (await outbox.AddAsync(FailedReport(), Requester, CancellationToken.None))
            .ShouldBeNull("the same request is answered once (a retry chain giving up again repeats nothing)");
    }

    [Test]
    public async Task AFailedBuildRunAgain_IsANewRequest_SoItsNextFailureIsToldAgain()
    {
        await FailureSent();

        // The retry of the task (TaskRetry: the same job id, queued again from Failed)
        Apply(BuildJob(), new EnqueuedState(), oldState: FailedState.StateName);

        (await Outbox().AddAsync(FailedReport(), Requester, CancellationToken.None)).ShouldNotBeNull();
    }

    [Test]
    public async Task AFailedBuildTaskRunAgain_IsANewRequest()
    {
        await FailureSent();

        Apply(Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(HeavyToolIds.Build, "1", "{}", null!, CancellationToken.None)),
            new ScheduledState(TimeSpan.FromMinutes(1)), path: Path, oldState: FailedState.StateName);

        (await Outbox().AddAsync(FailedReport(), Requester, CancellationToken.None)).ShouldNotBeNull();
    }

    [TestCase("Processing", TestName = "An attempt Hangfire retries stays the same request")]
    [TestCase("Scheduled", TestName = "The retry of an attempt, queued when due, stays the same request")]
    public async Task ARunThatDoesNotComeFromAFailure_KeepsTheRequest(string oldState)
    {
        await FailureSent();

        Apply(BuildJob(), oldState == ProcessingState.StateName ? new ScheduledState(TimeSpan.FromMinutes(1)) : new EnqueuedState(),
            oldState: oldState);

        (await Outbox().AddAsync(FailedReport(), Requester, CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task AFailedJobThatBuildsNoEntry_RunAgain_ChangesNothing()
    {
        await FailureSent();

        Apply(Job.FromExpression<DdbJobs>(j => j.Build(Org, Guid.Empty, null, false, null)), new EnqueuedState(),
            oldState: FailedState.StateName);

        (await Outbox().AddAsync(FailedReport(), Requester, CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task AFailedBuildDeleted_IsNotRunAgain_AndKeepsTheRequest()
    {
        await FailureSent();

        Apply(BuildJob(), new DeletedState(), oldState: FailedState.StateName);

        (await Outbox().AddAsync(FailedReport(), Requester, CancellationToken.None)).ShouldBeNull();
    }

    // --- A build that waits for files keeps one task ---

    // The row the job index has for a build job of the entry; JobIndexStateFilter, which runs first, has written the
    // final state of the job that ends
    private void Indexed(string jobId, int minutesAgo, string state = "Succeeded", string? hash = Hash)
    {
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        _context.JobIndices.Add(new JobIndex
        {
            JobId = jobId, OrgSlug = Org, DsSlug = Ds, Path = Path, Hash = hash, CurrentState = state,
            UserId = MagicStrings.AutoBuildServiceUserId, CreatedAtUtc = at, LastStateChangeUtc = at
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private string[] IndexedJobs() =>
        _context.JobIndices.AsNoTracking().Select(j => j.JobId).ToArray().Order(StringComparer.Ordinal).ToArray();

    private void EarlierAttemptsAndThisOne()
    {
        Indexed("attempt-1", 180);
        Indexed("attempt-2", 60);
        Indexed(JobId, 0);
    }

    [TestCase(true, TestName = "A build that still waits for files keeps one task, with e-mail on")]
    [TestCase(false, TestName = "A build that still waits for files keeps one task, with e-mail off")]
    public void EntryBuild_SucceededStillWaitingForFiles_RemovesTheTasksOfTheEarlierAttempts(bool emailOn)
    {
        _email.SetupGet(e => e.IsEnabled).Returns(emailOn);
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);
        EarlierAttemptsAndThisOne();

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        IndexedJobs().ShouldBe([JobId]);
        // The notification is as before: told when e-mail is on (the outbox tells a wait once)
        _reports.Select(r => r.Outcome).ShouldBe(emailOn ? [BuildOutcome.WaitingForFiles] : []);
    }

    [Test]
    public void BuildTask_SucceededStillWaitingForFiles_RemovesTheEarlierAttempts_ByTheHashOfTheEntry()
    {
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);
        Indexed("attempt-1", 180);
        Indexed("task-attempt", 60, hash: null);
        Indexed(JobId, 0, hash: null);

        // The task carries no hash: the entry's, from libddb
        Apply(Job.FromExpression<HeavyTaskJobWrapper>(w => w.Run(HeavyToolIds.Build, "1", "{}", null!, CancellationToken.None)),
            new SucceededState(null, 0, 0), hash: null, path: Path);

        IndexedJobs().ShouldBe([JobId]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void EntryBuild_SucceededAndBuilt_KeepsTheTasksOfItsEarlierBuilds(bool emailOn)
    {
        // A rebuild of a built entry: its earlier builds built it too
        _email.SetupGet(e => e.IsEnabled).Returns(emailOn);
        EarlierAttemptsAndThisOne();

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        IndexedJobs().ShouldBe(["attempt-1", "attempt-2", JobId]);
    }

    [Test]
    public void EntryBuild_SucceededWithAMarkerAFileAlreadyReleased_KeepsTheEarlierTasks()
    {
        // A missing file arrived after the build looked: the entry no longer waits, it builds now
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting(awaitingFiles: false)]);
        EarlierAttemptsAndThisOne();

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        IndexedJobs().ShouldBe(["attempt-1", "attempt-2", JobId]);
    }

    [Test]
    public void EntryBuild_Failed_KeepsTheEarlierTasks_AndWithEmailOffAsksLibddbNothing()
    {
        _email.SetupGet(e => e.IsEnabled).Returns(false);
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);
        Indexed("attempt-1", 180);
        Indexed("attempt-2", 60);
        Indexed(JobId, 0, state: "Failed");

        Apply(BuildJob(), Failed());

        IndexedJobs().ShouldBe(["attempt-1", "attempt-2", JobId]);
        _builds.Verify(b => b.GetBuild(It.IsAny<IDDB>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public void EmailOff_TheFirstBuildOfAnEntry_AsksLibddbNothing_EvenWhenItWaits()
    {
        // Nobody to tell and no earlier task to remove
        _email.SetupGet(e => e.IsEnabled).Returns(false);
        Entry(BuildState.Pending);
        _ddb.Setup(d => d.GetPendingBuildInfo()).Returns([Waiting()]);
        Indexed(JobId, 0);

        Apply(BuildJob(), new SucceededState(null, 0, 0));

        IndexedJobs().ShouldBe([JobId]);
        _builds.Verify(b => b.GetBuild(It.IsAny<IDDB>(), It.IsAny<string>()), Times.Never);
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
