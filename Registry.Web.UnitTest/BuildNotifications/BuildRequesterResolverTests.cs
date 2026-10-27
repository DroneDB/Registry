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
/// came from, and for an automatic retry the user whose build of the same content failed and was not built since.
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

    private Task<BuildRequester?> Resolve(string? userId, string? jobId) =>
        new BuildRequesterResolver(_db.NewContext()).ResolveAsync(
            new BuildReport(Org, Ds, Path, Hash, BuildOutcome.Completed, null, new BuildRequest(userId, jobId)),
            CancellationToken.None);

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
}
