#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Users;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// What the recorder puts in the outbox: only what the user's preference wants, never the same news twice, and the
/// digest scheduled once per window, by the first waiting line.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildNotificationRecorderTests"
/// </summary>
[TestFixture]
public class BuildNotificationRecorderTests : IDisposable
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

    [TestCase(BuildNotificationLevel.None, BuildOutcome.Completed, false)]
    [TestCase(BuildNotificationLevel.None, BuildOutcome.Failed, false)]
    [TestCase(BuildNotificationLevel.Failed, BuildOutcome.Completed, false)]
    [TestCase(BuildNotificationLevel.Failed, BuildOutcome.Failed, true)]
    [TestCase(BuildNotificationLevel.All, BuildOutcome.Completed, true)]
    [TestCase(BuildNotificationLevel.All, BuildOutcome.Failed, true)]
    public async Task TheLine_IsRecordedOnlyWhenThePreferenceWantsIt(BuildNotificationLevel level, BuildOutcome outcome,
        bool recorded)
    {
        _h.Levels["jane"] = level;

        await _h.Record("jane", "a.laz", outcome, "j1");

        _h.Rows().Count.ShouldBe(recorded ? 1 : 0);
        _h.Scheduled.Count.ShouldBe(recorded ? 1 : 0);
    }

    [Test]
    public async Task TheFirstLine_SchedulesTheDigestAfterTheWindow_WithWhatAnEmailShows()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;
        _h.Options.DigestMinutes = 7;

        await _h.Record("jane", "a.laz", BuildOutcome.Failed, "j1");

        _h.Scheduled.ShouldHaveSingleItem().ShouldBe(("jane", TimeSpan.FromMinutes(7)));
        var row = _h.Rows().ShouldHaveSingleItem();
        (row.UserId, row.OrgSlug, row.DsSlug, row.Path, row.Hash, row.Outcome, row.Error, row.RequestJobId)
            .ShouldBe(("jane", NotificationHarness.Org, NotificationHarness.Ds, "a.laz", "a.laz-hash", "Failed", "it broke", "j1"));
        row.FinishedAtUtc.ShouldBe(_h.Time.Now);
        row.SentAtUtc.ShouldBeNull();
        row.PendingKey.ShouldNotBeNull();
    }

    [Test]
    public async Task MoreLinesInTheWindow_DoNotScheduleAnotherDigest()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;

        await _h.Record("jane", "a.laz", BuildOutcome.Completed, "j1");
        await _h.Record("jane", "b.laz", BuildOutcome.Failed, "j2");
        _h.Levels["bob"] = BuildNotificationLevel.All;
        await _h.Record("bob", "c.laz", BuildOutcome.Completed, "j3");

        _h.Rows().Count.ShouldBe(3);
        _h.Scheduled.Select(s => s.UserId).ShouldBe(["jane", "bob"]);
    }

    [Test]
    public async Task AStateAppliedTwice_AddsOneLine()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;

        await _h.Record("jane", "a.laz", BuildOutcome.Completed, "j1");
        await _h.Record("jane", "a.laz", BuildOutcome.Completed, "j1");

        _h.Rows().ShouldHaveSingleItem();
        _h.Scheduled.ShouldHaveSingleItem();
    }

    [Test]
    public async Task TheSameNewsFromAnotherJob_WhileItWaits_AddsOneLine()
    {
        // A build skipped because another process held the lock sees the holder's build complete too
        _h.Levels["jane"] = BuildNotificationLevel.All;

        await _h.Record("jane", "a.laz", BuildOutcome.Completed, "holder");
        await _h.Record("jane", "a.laz", BuildOutcome.Completed, "skipped");

        _h.Rows().ShouldHaveSingleItem().RequestJobId.ShouldBe("holder");
    }

    [Test]
    public async Task TheSameRequestAndOutcome_AfterItWasSent_IsNotRecordedAgain()
    {
        // The retries of a request give up again at the next sweep: the user was already told
        _h.Levels["jane"] = BuildNotificationLevel.All;
        await _h.Record("jane", "a.laz", BuildOutcome.Failed, "user-build");
        await _h.DigestJob().SendAsync("jane", CancellationToken.None);

        await _h.Record("jane", "a.laz", BuildOutcome.Failed, "user-build");

        _h.Rows().ShouldHaveSingleItem().SentAtUtc.ShouldNotBeNull();
        _h.Scheduled.Count.ShouldBe(1);
    }

    [Test]
    public async Task TheSameNewsOfANewRequest_AfterTheFirstWasSent_IsRecordedAgain()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;
        await _h.Record("jane", "a.laz", BuildOutcome.Failed, "first");
        await _h.DigestJob().SendAsync("jane", CancellationToken.None);

        await _h.Record("jane", "a.laz", BuildOutcome.Failed, "second");

        _h.Rows().Count.ShouldBe(2);
        _h.Scheduled.Count.ShouldBe(2);
    }

    [Test]
    public async Task EmailOff_RecordsNothing_AndAsksNobody()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;
        _h.Email.SetupGet(e => e.IsEnabled).Returns(false);

        await _h.Record("jane", "a.laz", BuildOutcome.Failed, "j1");

        _h.Rows().ShouldBeEmpty();
        _h.Requesters.Verify(r => r.ResolveAsync(It.IsAny<BuildReport>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ABuildNobodyAskedFor_RecordsNothing()
    {
        await _h.Recorder().RecordAsync(NotificationHarness.Report(null!, "a.laz", BuildOutcome.Failed, "j1"),
            CancellationToken.None);

        _h.Rows().ShouldBeEmpty();
        _h.Scheduled.ShouldBeEmpty();
    }
}
