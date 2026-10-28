#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// Who a build notification goes to, read from a real (SQLite) job index: the job's user, the user of the task a build
/// came from, and for an automatic build the user whose build of the same content failed and was not built since, or else
/// whose build of it waited for files (the wait the outbox keeps).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildRequesterResolverTests"
/// </summary>
[TestFixture]
public class BuildRequesterResolverTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string Path = "cloud.laz";
    private const string Hash = "cloud-hash";
    private const string Auto = MagicStrings.AutoBuildServiceUserId;

    private static readonly DateTime T0 = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);

    private TestDb<RegistryContext> _db = null!;

    [SetUp]
    public void SetUp() => _db = TestDb.Create<RegistryContext>();

    [TearDown]
    public void TearDown() => _db.Dispose();

    private void Job(string jobId, string? userId, string state, int minute, string? parent = null, string hash = Hash,
        string path = Path)
    {
        using var db = _db.NewContext();
        var at = T0.AddMinutes(minute);
        db.JobIndices.Add(new JobIndex
        {
            JobId = jobId, OrgSlug = Org, DsSlug = Ds, Path = path, Hash = hash, UserId = userId, ParentJobId = parent,
            CurrentState = state, CreatedAtUtc = at,
            FailedAtUtc = state == "Failed" ? at.AddSeconds(30) : null,
            SucceededAtUtc = state == "Succeeded" ? at.AddSeconds(30) : null
        });
        db.SaveChanges();
    }

    private Task<BuildRequester?> Resolve(string? userId, string? jobId) => Resolve(new BuildRequest(userId, jobId));

    private Task<BuildRequester?> Resolve(BuildRequest request)
    {
        var db = _db.NewContext();
        return new BuildRequesterResolver(db, new BuildNotificationOutbox(db, TimeProvider.System)).ResolveAsync(
            new BuildReport(Org, Ds, Path, Hash, BuildOutcome.Completed, null, request), CancellationToken.None);
    }

    // The wait for files the outbox keeps of the build of userId's request jobId (wanted or not, sent or not)
    private void Waited(string userId, string jobId, string hash = Hash, bool sent = true)
    {
        using var db = _db.NewContext();
        db.BuildNotifications.Add(new BuildNotification
        {
            UserId = userId, OrgSlug = Org, DsSlug = Ds, Path = Path, Hash = hash,
            Outcome = nameof(BuildOutcome.WaitingForFiles), Error = "cloud.txt", RequestJobId = jobId,
            FinishedAtUtc = T0, SentAtUtc = sent ? T0 : null, PendingKey = sent ? null : jobId
        });
        db.SaveChanges();
    }

    // The pending entries whose automatic retries ran out (FinishedBuildReporter.ReportGivenUpAsync)
    private Task<BuildRequester?> ResolveGivenUp() => Resolve(BuildRequest.RetriesGivenUp);

    [Test]
    public async Task AUsersBuild_GoesToThatUser()
    {
        (await Resolve("jane", "j1")).ShouldBe(new BuildRequester("jane", "j1"));
    }

    [Test]
    public async Task ABuildOfATaskOutput_GoesToTheUserOfTheTask()
    {
        Job("task", "jane", "Succeeded", 0);
        Job("child", null, "Succeeded", 1, parent: "task");

        (await Resolve(null, "child")).ShouldBe(new BuildRequester("jane", "child"));
    }

    [Test]
    public async Task ABuildWithNoUserAndNoTask_GoesToNobody()
    {
        Job("orphan", null, "Succeeded", 0);

        (await Resolve(null, "orphan")).ShouldBeNull();
        (await Resolve(null, null)).ShouldBeNull();
    }

    [Test]
    public async Task ARetry_GoesToTheUserWhoseBuildOfTheSameContentFailed_AndAnswersThatRequest()
    {
        Job("user-build", "jane", "Failed", 0);
        Job("retry-1", Auto, "Failed", 2);
        Job("retry-2", Auto, "Succeeded", 5);

        (await Resolve(Auto, "retry-2")).ShouldBe(new BuildRequester("jane", "user-build"));
    }

    [Test]
    public async Task ARetry_OfAFailedTaskOutput_GoesToTheUserOfTheTask()
    {
        Job("task", "jane", "Succeeded", 0);
        Job("child", null, "Failed", 1, parent: "task");

        (await Resolve(Auto, "retry")).ShouldBe(new BuildRequester("jane", "child"));
    }

    [Test]
    public async Task ARescanOfABuiltEntry_GoesToNobody()
    {
        // The user's latest build of this content succeeded: an automatic rebuild is nobody's request
        Job("old-user-build", "jane", "Failed", 0);
        Job("user-build", "jane", "Succeeded", 10);

        (await Resolve(Auto, "rescan")).ShouldBeNull();
    }

    [Test]
    public async Task ARebuildAfterTheRetriesBuiltIt_GoesToNobody()
    {
        // The retry chain built the entry; a later sweep of the same content is not the user's request any more
        Job("user-build", "jane", "Failed", 0);
        Job("retry-1", Auto, "Succeeded", 5);

        (await Resolve(Auto, "completeness-sweep")).ShouldBeNull();
    }

    [Test]
    public async Task ARetryOfOtherContent_GoesToNobody()
    {
        // The file was replaced: the failed build was of another hash
        Job("user-build", "jane", "Failed", 0, hash: "old-hash");

        (await Resolve(Auto, "retry")).ShouldBeNull();
    }

    [Test]
    public async Task ARetryWithNoUserBuildBefore_GoesToNobody()
    {
        Job("retry-1", Auto, "Failed", 0);

        (await Resolve(Auto, "retry-2")).ShouldBeNull();
    }

    // --- The retries gave up ---

    [Test]
    public async Task GivenUp_AfterAFailedUserBuild_GoesToThatUser()
    {
        Job("user-build", "jane", "Failed", 0);
        Job("retry-1", Auto, "Failed", 2);

        (await ResolveGivenUp()).ShouldBe(new BuildRequester("jane", "user-build"));
    }

    [Test]
    public async Task GivenUp_AfterAUserBuildThatWaitedForFiles_GoesToThatUser()
    {
        // The user's build found files missing (a success that built nothing); the files arrived, released the build,
        // and its retries failed until the budget ran out
        Job("user-build", "jane", "Succeeded", 0);
        Waited("jane", "user-build");
        Job("release-build", Auto, "Failed", 30);

        (await ResolveGivenUp()).ShouldBe(new BuildRequester("jane", "user-build"));
    }

    [Test]
    public async Task GivenUp_AfterAUserBuildThatSucceeded_WithoutAWait_GoesToNobody()
    {
        // A success is not a wait: only the wait the outbox keeps says that the build found files missing
        Job("user-build", "jane", "Succeeded", 0);
        Job("retry-1", Auto, "Failed", 2);

        (await ResolveGivenUp()).ShouldBeNull();
    }

    [Test]
    public async Task GivenUp_OfAnEntryNoUserAskedFor_GoesToNobody()
    {
        // A rescan or a completeness sweep found the entry; its retries ran out
        Job("sweep-build", Auto, "Succeeded", 0);
        Job("retry-1", Auto, "Succeeded", 2);

        (await ResolveGivenUp()).ShouldBeNull();
    }

    [Test]
    public async Task GivenUp_OfOtherContent_GoesToNobody()
    {
        // The user's build was of the file before it was replaced
        Job("user-build", "jane", "Succeeded", 0, hash: "old-hash");

        (await ResolveGivenUp()).ShouldBeNull();
    }

    [Test]
    public async Task GivenUp_AfterAFailedUserBuildThatWasBuiltSince_GoesToNobody()
    {
        Job("user-build", "jane", "Failed", 0);
        Job("retry-1", Auto, "Succeeded", 5);

        (await ResolveGivenUp()).ShouldBeNull();
    }

    // --- A build released by the files it waited for ---

    [Test]
    public async Task TheBuildThatFollowsTheFiles_GoesToTheUserWhoseBuildWaited_AndAnswersThatRequest()
    {
        // The user's build found the files missing and succeeded without building; the upload of a missing file released
        // the entry, and the pending-build coordinator built it as the build service
        Job("user-build", "jane", "Succeeded", 0);
        Waited("jane", "user-build");

        (await Resolve(Auto, "release-build")).ShouldBe(new BuildRequester("jane", "user-build"));
    }

    [Test]
    public async Task TheBuildThatFollowsTheFiles_GoesToTheUserWhoseBuildWaited_AfterTheJobIndexForgotIt()
    {
        // The wait outlives the job history: the files may come months later
        Waited("jane", "user-build");

        (await Resolve(Auto, "release-build")).ShouldBe(new BuildRequester("jane", "user-build"));
    }

    [Test]
    public async Task TheBuildThatFollowsTheFiles_GoesToTheFirstRequestThatWaited()
    {
        Waited("jane", "first-build");
        Waited("bob", "second-build", sent: false);

        (await Resolve(Auto, "release-build")).ShouldBe(new BuildRequester("jane", "first-build"));
    }

    [Test]
    public async Task ABuildOfOtherContent_IsNotTheBuildThatFollowsTheFiles()
    {
        Waited("jane", "user-build", hash: "old-hash");

        (await Resolve(Auto, "release-build")).ShouldBeNull();
    }

    [Test]
    public async Task AFailedUserRequestAfterTheWait_IsTheOneAnswered()
    {
        // The latest request is the user's that failed: the retries that follow are that request's
        Job("user-build", "jane", "Succeeded", 0);
        Waited("jane", "user-build");
        Job("bob-build", "bob", "Failed", 10);

        (await Resolve(Auto, "retry")).ShouldBe(new BuildRequester("bob", "bob-build"));
    }

    [Test]
    public async Task ARetryJob_AfterAUserBuildThatSucceeded_GoesToNobody()
    {
        // Only the end of the retries credits a success: a retry job that ends cannot tell a success that left the entry
        // pending from one that built it
        Job("user-build", "jane", "Succeeded", 0);

        (await Resolve(Auto, "retry-1")).ShouldBeNull();
    }
}
