using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Users;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// A build that finds files missing, from the report the classifier makes to the e-mail: the requester is told once per
/// content of the entry (with the failures of their preference), whatever host or attempt reports it again, and the build
/// that runs when the files arrive (as the build service) answers that requester, with the real resolver over the job
/// index and the outbox.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~WaitingForFilesNotificationTests"
/// </summary>
[TestFixture]
public class WaitingForFilesNotificationTests : IDisposable
{
    private const string Path = "model.obj";
    private const string Hash = "model-hash";
    private const string Auto = MagicStrings.AutoBuildServiceUserId;

    private NotificationHarness _h = null!;

    [SetUp]
    public void SetUp() => _h = new NotificationHarness { RequestersFromJobIndex = true };

    [TearDown]
    public void TearDown() => _h.Dispose();

    public void Dispose()
    {
        _h?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static JobConsole NoConsole => JobConsole.For(NullLogger.Instance, null);

    // The end of a build job as the reporter records it: who the job carries, and its outcome
    private Task Ended(string userId, string jobId, BuildOutcome outcome) =>
        _h.Record(userId, Path, outcome, jobId, Hash);

    // jane's upload of the .obj: her build finds the .mtl and a texture missing and ends without building
    private async Task JaneWaits(BuildNotificationLevel level)
    {
        _h.Levels["jane"] = level;
        _h.IndexedBuild("jane-build", "jane", "Succeeded", 0, Path, Hash);
        await Ended("jane", "jane-build", BuildOutcome.WaitingForFiles);
    }

    private async Task Digest(string userId = "jane")
    {
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await _h.DigestJob().SendAsync(userId, CancellationToken.None);
    }

    private BuildNotificationLine[] Told() => [.. _h.Sent.SelectMany(s => s.Model.Builds)];

    [TestCase(BuildNotificationLevel.Failed)]
    [TestCase(BuildNotificationLevel.All)]
    public async Task TheFirstWait_IsToldOnce_WithTheMissingFiles(BuildNotificationLevel level)
    {
        await JaneWaits(level);
        await Digest();

        var (to, model) = _h.Sent.ShouldHaveSingleItem();
        to.Address.ShouldBe("jane@example.com");
        var line = model.Builds.ShouldHaveSingleItem();
        (line.Path, line.Outcome, line.Error).ShouldBe((Path, BuildOutcome.WaitingForFiles, NotificationHarness.MissingFiles));
        model.Summary.ShouldBe("1 build waiting for files");
    }

    [Test]
    public async Task ALaterWaitOfTheSameContent_IsNotToldAgain_WhoeverReportsIt()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();

        // Her own rebuild finds the files missing again; so does the build the upload of an unrelated file released
        _h.IndexedBuild("jane-rebuild", "jane", "Succeeded", 30, Path, Hash);
        await Ended("jane", "jane-rebuild", BuildOutcome.WaitingForFiles);
        await Ended(Auto, "coordinator-build", BuildOutcome.WaitingForFiles);
        // Bob asks for it too
        _h.Levels["bob"] = BuildNotificationLevel.All;
        await Ended("bob", "bob-build", BuildOutcome.WaitingForFiles);
        await Digest();
        await Digest("bob");

        _h.Sent.Count.ShouldBe(1);
        _h.Rows().Count(r => r.SentAtUtc == null).ShouldBe(0);
        _h.Scheduled.Count.ShouldBe(1);
    }

    [Test]
    public async Task ALaterWaitOfTheSameContent_IsNotToldAgain_EvenAfterTheSentLinesRetention()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();

        // The daily cleanup of the sent lines does not end a wait: the files may come months later
        _h.Time.Advance(BuildNotificationCleanup.SentRetention + TimeSpan.FromDays(1));
        await _h.Cleanup().RunAsync(NoConsole, CancellationToken.None);
        await Ended("jane", "jane-rebuild", BuildOutcome.WaitingForFiles);
        await Digest();

        _h.Sent.Count.ShouldBe(1);
        _h.Rows().ShouldHaveSingleItem().Outcome.ShouldBe(nameof(BuildOutcome.WaitingForFiles));
    }

    [Test]
    public async Task TheWaitOfADeletedDataset_GoesWithTheOtherSentLines()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();
        _h.Change(db => db.BuildNotifications.Single().DsSlug = SlugKeyedRows.TombstoneSlug(Guid.NewGuid()));

        _h.Time.Advance(BuildNotificationCleanup.SentRetention + TimeSpan.FromDays(1));
        await _h.Cleanup().RunAsync(NoConsole, CancellationToken.None);

        _h.Rows().ShouldBeEmpty();
    }

    [Test]
    public async Task NoneWanted_NothingIsTold()
    {
        await JaneWaits(BuildNotificationLevel.None);
        await Digest();

        _h.Sent.ShouldBeEmpty();
        _h.Scheduled.ShouldBeEmpty();
        // Kept as a line no e-mail tells: it names who the build that follows the files answers
        _h.Rows().ShouldHaveSingleItem().SentAtUtc.ShouldNotBeNull();
    }

    // --- The build that runs when the files arrive ---

    [Test]
    public async Task TheBuildThatFollowsTheFiles_TellsTheRequesterItCompleted()
    {
        await JaneWaits(BuildNotificationLevel.All);
        await Digest();

        // The upload of the .mtl released the entry; the pending-build coordinator built it as the build service
        _h.IndexedBuild("release-build", Auto, "Succeeded", 60, Path, Hash);
        await Ended(Auto, "release-build", BuildOutcome.Completed);
        await Digest();

        _h.Sent.Count.ShouldBe(2);
        var line = _h.Sent.Last().Model.Builds.ShouldHaveSingleItem();
        (line.Path, line.Outcome).ShouldBe((Path, BuildOutcome.Completed));
        _h.Rows().ShouldHaveSingleItem().RequestJobId.ShouldBe("jane-build", "it answers the request that waited");
    }

    [Test]
    public async Task TheBuildThatFollowsTheFiles_TellsTheRequesterItFailed()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();

        _h.IndexedBuild("release-build", Auto, "Failed", 60, Path, Hash);
        await Ended(Auto, "release-build", BuildOutcome.Failed);
        await Digest();

        var line = _h.Sent.Last().Model.Builds.ShouldHaveSingleItem();
        (line.Outcome, line.Error).ShouldBe((BuildOutcome.Failed, "it broke"));
    }

    [Test]
    public async Task TheBuildThatFollowsTheFiles_FollowsThePreferenceOfItsEnd()
    {
        // No e-mails when the build waited; all of them when it completed
        await JaneWaits(BuildNotificationLevel.None);
        _h.Levels["jane"] = BuildNotificationLevel.All;

        await Ended(Auto, "release-build", BuildOutcome.Completed);
        await Digest();

        _h.Sent.ShouldHaveSingleItem().Model.Builds.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.Completed);
    }

    [Test]
    public async Task TheBuildThatFollowsTheFiles_CompletedForAUserWhoWantsOnlyFailures_TellsNothing()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();

        await Ended(Auto, "release-build", BuildOutcome.Completed);

        _h.Sent.Count.ShouldBe(1);
        _h.Rows().ShouldBeEmpty("the wait ended with the build");
    }

    [Test]
    public async Task FilesThatArriveBeforeTheDigest_TellOnlyTheCompletion()
    {
        await JaneWaits(BuildNotificationLevel.All);

        await Ended(Auto, "release-build", BuildOutcome.Completed);
        await Digest();

        _h.Sent.ShouldHaveSingleItem().Model.Builds.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.Completed);
    }

    [Test]
    public async Task AWaitAfterTheBuildEnded_IsNewsAgain()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();
        await Ended(Auto, "release-build", BuildOutcome.Failed);
        await Digest();

        // Her forced rebuild of the same content finds a file missing again
        await Ended("jane", "jane-rebuild", BuildOutcome.WaitingForFiles);
        await Digest();

        _h.Sent.Count.ShouldBe(3);
        _h.Sent.Last().Model.Builds.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.WaitingForFiles);
    }

    // ---- the entry or its dataset goes, and the same file comes back

    // jane uploads the same .obj again (same path, same content) and its build finds the files missing again
    private async Task JaneUploadsItAgain()
    {
        _h.IndexedBuild("jane-reupload", "jane", "Succeeded", 60, Path, Hash);
        await Ended("jane", "jane-reupload", BuildOutcome.WaitingForFiles);
        await Digest();
    }

    [Test]
    public async Task TheSameFile_DeletedAndUploadedAgain_IsToldAgain()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();

        await _h.EntriesLeft(Path);
        _h.Rows().ShouldBeEmpty();
        await JaneUploadsItAgain();

        _h.Sent.Count.ShouldBe(2);
        _h.Sent.Last().Model.Builds.ShouldHaveSingleItem().Outcome.ShouldBe(BuildOutcome.WaitingForFiles);
    }

    [Test]
    public async Task TheSameFile_PutBackAfterItsFolderWasDeleted_IsToldAgain_TheWaitsOfOtherEntriesStay()
    {
        const string inFolder = "site/model.obj";
        _h.Levels["jane"] = BuildNotificationLevel.Failed;
        _h.IndexedBuild("jane-folder", "jane", "Succeeded", 0, inFolder, Hash);
        await _h.Record("jane", inFolder, BuildOutcome.WaitingForFiles, "jane-folder", Hash);
        // An entry whose name starts like the folder, and one of the same name in another dataset
        _h.IndexedBuild("jane-sibling", "jane", "Succeeded", 0, "site2/model.obj", Hash);
        await _h.Record("jane", "site2/model.obj", BuildOutcome.WaitingForFiles, "jane-sibling", Hash);
        _h.Change(db => db.JobIndices.Add(new JobIndex
        {
            JobId = "jane-other-ds", OrgSlug = NotificationHarness.Org, DsSlug = "other", Path = inFolder, Hash = Hash,
            UserId = "jane", CurrentState = "Succeeded", CreatedAtUtc = _h.Time.Now, SucceededAtUtc = _h.Time.Now
        }));
        await _h.Recorder().RecordAsync(NotificationHarness.Report("jane", inFolder, BuildOutcome.WaitingForFiles,
            "jane-other-ds", Hash) with { DsSlug = "other" }, CancellationToken.None);
        await Digest();

        await _h.EntriesLeft("site");

        _h.Rows().Select(r => (r.DsSlug, r.Path)).Order()
            .ShouldBe([(NotificationHarness.Ds, "site2/model.obj"), ("other", inFolder)]);
        _h.IndexedBuild("jane-folder-again", "jane", "Succeeded", 60, inFolder, Hash);
        await _h.Record("jane", inFolder, BuildOutcome.WaitingForFiles, "jane-folder-again", Hash);
        await Digest();
        _h.Sent.Last().Model.Builds.ShouldHaveSingleItem().Path.ShouldBe(inFolder);
    }

    [Test]
    public async Task AnEntryMovedAway_EndsTheWaitOfItsOldPath()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();

        await _h.EntriesLeft(Path);
        // The build that follows the files at the old path answers nobody's request any more
        (await _h.Outbox().WaitRequesterAsync(NotificationHarness.Org, NotificationHarness.Ds, Path, Hash,
            CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task TheSameFile_UploadedToADatasetCreatedAgainWithTheSlugOfADeletedOne_IsToldAgain()
    {
        await JaneWaits(BuildNotificationLevel.Failed);
        await Digest();

        // The deletion of the dataset (DatasetCommands.Delete): its rows go to its tombstone
        await _h.ChangeAsync(db => db.FollowDatasetAsync(NotificationHarness.Org, NotificationHarness.Ds,
            NotificationHarness.Org, SlugKeyedRows.TombstoneSlug(Guid.NewGuid())));
        await JaneUploadsItAgain();

        _h.Sent.Count.ShouldBe(2);
    }

    [Test]
    public async Task AnAutomaticBuildOfAnEntryNobodyAskedFor_TellsNobody()
    {
        _h.Levels["jane"] = BuildNotificationLevel.All;

        // A rescan finds the files missing; the build that follows them is nobody's request either
        await Ended(Auto, "rescan-build", BuildOutcome.WaitingForFiles);
        await Ended(Auto, "release-build", BuildOutcome.Completed);

        _h.Rows().ShouldBeEmpty();
        Told().ShouldBeEmpty();
    }
}
