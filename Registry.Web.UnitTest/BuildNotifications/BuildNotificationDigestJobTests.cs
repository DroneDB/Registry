#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Moq;
using NUnit.Framework;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Users;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// The digest: one e-mail with every waiting line of a user, sent once even when two runs for the user overlap (the
/// claim), the preference read again at send time, the lines that arrive during a send left to a new digest, and the
/// cleanup of the outbox.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildNotificationDigestJobTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public class BuildNotificationDigestJobTests : IDisposable
{
    private NotificationHarness _h = null!;

    [SetUp]
    public void SetUp() => _h = new NotificationHarness();

    [TearDown]
    public void TearDown() => _h.Dispose();

    public void Dispose()
    {
        _h?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task TwoLines(BuildNotificationLevel level = BuildNotificationLevel.All)
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;
        await _h.Record("jane", "a.laz", BuildOutcome.Completed, "j1");
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await _h.Record("jane", "b.tif", BuildOutcome.Failed, "j2");
        _h.Levels["jane"] = level;
    }

    private Task Digest() => _h.DigestJob().SendAsync("jane", CancellationToken.None);

    [Test]
    public async Task TheDigest_SendsEveryWaitingLineInOneEmail_AndMarksThemSent()
    {
        await TwoLines();

        await Digest();

        var (to, model) = _h.Sent.ShouldHaveSingleItem();
        to.ShouldBe(new EmailAddress("jane@example.com", "jane"));
        model.UserName.ShouldBe("jane");
        model.Builds.Select(b => (b.Path, b.Outcome, b.Error)).ShouldBe(
            [("a.laz", BuildOutcome.Completed, null), ("b.tif", BuildOutcome.Failed, "it broke")]);
        model.Builds.ShouldAllBe(b => b.FinishedAtUtc.Kind == DateTimeKind.Utc && b.Dataset == "org/ds");
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null && r.PendingKey == null);

        // Nothing waits: no further digest
        _h.Scheduled.Count.ShouldBe(1);
    }

    [Test]
    public async Task TwoOverlappingDigests_SendEachLineOnce()
    {
        await TwoLines();

        // The first run is inside the send (its lines claimed, not yet sent) when a second run for the same user, from
        // another host, runs to its end. Without the claim the second would read the same unsent lines and send them too.
        var secondRan = false;
        var first = _h.DigestJob();
        _h.WhileComposing = async () =>
        {
            _h.WhileComposing = null;
            await _h.DigestJob().SendAsync("jane", CancellationToken.None);
            secondRan = true;
        };

        await first.SendAsync("jane", CancellationToken.None);

        secondRan.ShouldBeTrue();
        _h.Sent.ShouldHaveSingleItem().Model.Builds.Count.ShouldBe(2);
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task ThePreferenceTurnedOffBeforeTheDigest_SendsNothing_AndDropsTheLines()
    {
        await TwoLines(BuildNotificationLevel.None);

        await Digest();

        _h.Sent.ShouldBeEmpty();
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task ThePreferenceChangedToFailures_SendsOnlyTheFailures()
    {
        await TwoLines(BuildNotificationLevel.Failed);

        await Digest();

        _h.Sent.ShouldHaveSingleItem().Model.Builds.ShouldHaveSingleItem().Path.ShouldBe("b.tif");
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task AUserWithoutAnAddress_GetsNothing_AndTheLinesAreDropped()
    {
        await TwoLines();
        _h.WithoutAddress.Add("jane");

        await Digest();

        _h.Sent.ShouldBeEmpty();
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task ALineOfADatasetDeletedSince_IsLeftOut()
    {
        await TwoLines();
        _h.Change(db => db.BuildNotifications.Single(r => r.Path == "a.laz").DsSlug =
            Data.SlugKeyedRows.TombstoneSlug(Guid.NewGuid()));

        await Digest();

        _h.Sent.ShouldHaveSingleItem().Model.Builds.ShouldHaveSingleItem().Path.ShouldBe("b.tif");
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task ALineOfADatasetTheUserCanNoLongerRead_IsLeftOut_AndEachDatasetIsAskedOnce()
    {
        // Removed from the organization (or the dataset made private) since the build ended
        await TwoLines();
        await _h.Record("jane", "c.laz", BuildOutcome.Completed, "j3");
        _h.Change(db =>
        {
            foreach (var row in db.BuildNotifications.Where(r => r.Path != "a.laz"))
                row.DsSlug = "secret";
        });
        _h.Unreadable.Add("jane/org/secret");

        await Digest();

        _h.Sent.ShouldHaveSingleItem().Model.Builds.ShouldHaveSingleItem().Path.ShouldBe("a.laz");
        _h.AccessChecks.ShouldBe(2);
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task NoDatasetTheUserCanStillRead_SendsNothing_AndDropsTheLines()
    {
        await TwoLines();
        _h.Unreadable.Add("jane/org/ds");

        await Digest();

        _h.Sent.ShouldBeEmpty();
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task EmailTurnedOffBeforeTheDigest_SendsNothing()
    {
        await TwoLines();
        _h.Email.SetupGet(e => e.IsEnabled).Returns(false);

        await Digest();

        _h.Sent.ShouldBeEmpty();
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task ALineRecordedDuringTheSend_GetsANewDigest_AndIsNotLost()
    {
        await TwoLines();
        _h.Scheduled.Clear();
        _h.WhileComposing = async () =>
        {
            _h.WhileComposing = null;
            _h.Time.Advance(TimeSpan.FromMinutes(1));
            await _h.Record("jane", "c.obj", BuildOutcome.Completed, "j3");
            // The run sending the others is still on: the recorder leaves the next digest to it
            _h.Scheduled.ShouldBeEmpty();
        };

        await Digest();

        _h.Sent.ShouldHaveSingleItem().Model.Builds.Count.ShouldBe(2);
        var late = _h.Rows().Single(r => r.Path == "c.obj");
        late.SentAtUtc.ShouldBeNull();
        late.ClaimId.ShouldBeNull();
        // Due a full window after the line it is for
        _h.Scheduled.ShouldHaveSingleItem().ShouldBe(("jane", TimeSpan.FromMinutes(_h.Options.DigestMinutes)));

        await Digest();
        _h.Sent.Count.ShouldBe(2);
        _h.Sent.Last().Model.Builds.ShouldHaveSingleItem().Path.ShouldBe("c.obj");
    }

    [Test]
    public async Task AFailedSend_GivesTheLinesBack_ForTheRetry()
    {
        await TwoLines();
        _h.WhileQueueing = () => throw new BackgroundJobClientException("job storage unavailable", new InvalidOperationException("connection lost"));

        await Should.ThrowAsync<BackgroundJobClientException>(Digest);

        _h.Rows().ShouldAllBe(r => r.SentAtUtc == null && r.ClaimId == null && r.ClaimedAtUtc == null);

        _h.WhileQueueing = null;
        await Digest();
        _h.Sent.ShouldHaveSingleItem().Model.Builds.Count.ShouldBe(2);
    }

    [Test]
    public async Task AMarkThatFails_QueuesNoEmail_AndTheRetrySendsItOnce()
    {
        await TwoLines();
        _h.Faults.FailNextMark = true;

        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(Digest);

        // The lines are marked sent before their e-mail is queued: a mark that fails leaves nothing queued
        _h.Sent.ShouldBeEmpty();
        _h.Rows().ShouldAllBe(r => r.SentAtUtc == null && r.ClaimId == null);

        await Digest();
        _h.Sent.ShouldHaveSingleItem().Model.Builds.Count.ShouldBe(2);
        _h.Rows().ShouldAllBe(r => r.SentAtUtc != null);
    }

    [Test]
    public async Task ACommitThatFailsAfterTheEmailIsQueued_KeepsTheClaim_SoTheRetrySendsNothingAgain()
    {
        await TwoLines();
        _h.Faults.FailNextCommit = true;

        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(Digest);

        _h.Sent.ShouldHaveSingleItem();
        // The mark is rolled back with the commit, but the lines are not given back: their e-mail is out
        _h.Rows().ShouldAllBe(r => r.SentAtUtc == null && r.ClaimId != null);

        await Digest();
        _h.Sent.ShouldHaveSingleItem();
    }

    // --- Cleanup ---

    [Test]
    public async Task Cleanup_DeletesTheLinesSentLongAgo_AndKeepsTheOthers()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;
        await _h.Record("jane", "old.laz", BuildOutcome.Completed, "j1");
        await Digest();
        _h.Time.Advance(BuildNotificationCleanup.SentRetention + TimeSpan.FromHours(1));
        await _h.Record("jane", "recent.laz", BuildOutcome.Completed, "j2");
        await Digest();
        await _h.Record("jane", "waiting.laz", BuildOutcome.Completed, "j3");
        _h.Scheduled.Clear();

        await _h.Cleanup().RunAsync(NoConsole, CancellationToken.None);

        _h.Rows().Select(r => r.Path).ShouldBe(["recent.laz", "waiting.laz"]);
        // The waiting line is not overdue: its digest is still due
        _h.Scheduled.ShouldBeEmpty();
    }

    [Test]
    public async Task Cleanup_SchedulesTheDigestOfLinesLeftWaiting_AndReleasesTheClaimOfADeadRun()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;
        _h.Levels["bob"] = BuildNotificationLevel.All;
        await _h.Record("jane", "a.laz", BuildOutcome.Completed, "j1");
        await _h.Record("bob", "b.laz", BuildOutcome.Completed, "j2");
        // A digest run of bob died holding his line
        _h.Change(db => db.BuildNotifications.Single(r => r.UserId == "bob").ClaimId = "dead-run");
        _h.Change(db => db.BuildNotifications.Single(r => r.UserId == "bob").ClaimedAtUtc = _h.Time.Now);
        _h.Time.Advance(BuildNotificationCleanup.OverdueAfter + TimeSpan.FromMinutes(1));
        _h.Scheduled.Clear();

        await _h.Cleanup().RunAsync(NoConsole, CancellationToken.None);

        _h.Scheduled.Select(s => (s.UserId, s.Delay)).OrderBy(s => s.UserId)
            .ShouldBe([("bob", TimeSpan.Zero), ("jane", TimeSpan.Zero)]);
        _h.Rows().Single(r => r.UserId == "bob").ClaimId.ShouldBeNull();

        await _h.DigestJob().SendAsync("bob", CancellationToken.None);
        _h.Sent.ShouldHaveSingleItem().To.Address.ShouldBe("bob@example.com");
    }

    // --- The job ---

    [Test]
    public void Job_Payload_RoundTripsThroughTheJobStorage()
    {
        // The serializer settings of the hosts (RegistryJobsServiceCollectionExtensions)
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();

        var payload = InvocationData.SerializeJob(
            Job.FromExpression<BuildNotificationDigestJob>(j => j.SendAsync("jane", CancellationToken.None))).SerializePayload();
        var job = InvocationData.DeserializePayload(payload).DeserializeJob();

        job.Type.ShouldBe(typeof(BuildNotificationDigestJob));
        job.Method.Name.ShouldBe(nameof(BuildNotificationDigestJob.SendAsync));
        job.Args[0].ShouldBe("jane");
    }

    [Test]
    public void Job_RetriesAFewTimesWithinHalfAnHour_OnTheDefaultQueue()
    {
        var job = Job.FromExpression<BuildNotificationDigestJob>(j => j.SendAsync("jane", CancellationToken.None));
        var filters = JobFilterProviders.Providers.GetFilters(job).Select(f => f.Instance).ToList();

        var retry = filters.OfType<AutomaticRetryAttribute>().ShouldHaveSingleItem();
        retry.Attempts.ShouldBe(3);
        // Well before the cleanup counts the lines as lost (an hour)
        retry.DelaysInSeconds.Sum().ShouldBeLessThan((int)BuildNotificationCleanup.OverdueAfter.TotalSeconds / 2);
        filters.OfType<QueueAttribute>().ShouldBeEmpty();
    }

    private static Services.Jobs.JobConsole NoConsole =>
        Services.Jobs.JobConsole.For(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, null);
}
