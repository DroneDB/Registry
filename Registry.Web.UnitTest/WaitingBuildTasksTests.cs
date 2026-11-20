using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// A build that waits for files keeps one task. libddb defers a build that finds files missing with a normal return, so
/// every attempt before the files arrive ends succeeded without building: of the succeeded build jobs of the entry for
/// that content only the newest stays (<see cref="JobIndexWriter.DeleteEarlierSucceededBuildsAsync"/>), which the task
/// lists show as the task that waits.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~WaitingBuildTasksTests"
/// </summary>
[TestFixture]
public class WaitingBuildTasksTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string Path = "site/model.obj";
    private const string Hash = "model-hash";

    private static readonly DateTime Start = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    private RegistryContext _context = null!;
    private Mock<IRecordCache> _cache = null!;
    private JobIndexWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestDb.CreateContext<RegistryContext>();
        _cache = new Mock<IRecordCache>();
        _writer = new JobIndexWriter(_context, NullLogger<JobIndexWriter>.Instance, _cache.Object);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    // A job of the entry, created `hour` hours after the start
    private void Job(string id, int hour, string state = "Succeeded", string? path = Path, string? hash = Hash,
        string org = Org, string ds = Ds, string toolId = HeavyToolIds.Build)
    {
        var at = Start.AddHours(hour);
        _context.JobIndices.Add(new JobIndex
        {
            JobId = id, OrgSlug = org, DsSlug = ds, Path = path, Hash = hash, ToolId = toolId, CurrentState = state,
            UserId = MagicStrings.AutoBuildServiceUserId, CreatedAtUtc = at, LastStateChangeUtc = at,
            SucceededAtUtc = state == "Succeeded" ? at : null
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private string[] Jobs() =>
        _context.JobIndices.AsNoTracking().Select(j => j.JobId).ToArray().Order(StringComparer.Ordinal).ToArray();

    private Task<int> KeepLatest() => _writer.DeleteEarlierSucceededBuildsAsync(Org, Ds, Path, Hash);

    [Test]
    public async Task TheNewestAttemptStays_TheEarlierOnesAreRemoved()
    {
        Job("first", 0);
        Job("third", 30);
        Job("second", 2);

        (await KeepLatest()).ShouldBe(2);

        Jobs().ShouldBe(["third"]);
    }

    [Test]
    public async Task OneAttempt_NothingIsRemoved_AndTheListIsNotInvalidated()
    {
        Job("only", 0);

        (await KeepLatest()).ShouldBe(0);

        Jobs().ShouldBe(["only"]);
        _cache.Verify(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task TheTaskListOfTheDatasetIsInvalidated()
    {
        Job("first", 0);
        Job("second", 1);

        await KeepLatest();

        _cache.Verify(c => c.RemoveAsync(CacheCategories.TasksList(Org, Ds), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>A build task of the tasks API carries no hash: it built the content the entry had, so it is an attempt.</summary>
    [TestCase(null)]
    [TestCase("")]
    public async Task AJobWithoutAHash_IsAnAttemptOfTheContent(string? noHash)
    {
        Job("unnamed-earlier", 0, hash: noHash);
        Job("named", 1);
        Job("unnamed-newest", 2, hash: noHash);

        (await KeepLatest()).ShouldBe(2);

        Jobs().ShouldBe(["unnamed-newest"]);
    }

    [Test]
    public async Task TwoAttemptsCreatedAtTheSameTime_OneStays()
    {
        Job("a", 0);
        Job("b", 0);

        (await KeepLatest()).ShouldBe(1);

        Jobs().ShouldBe(["b"]);
    }

    /// <summary>What is not an earlier attempt of this build stays, older or not.</summary>
    [Test]
    public async Task OtherJobsStay()
    {
        Job("other-path", 0, path: "site/other.obj");
        Job("other-case", 0, path: "site/Model.obj");
        Job("folder-prefix", 0, path: "site/model.obj.bak");
        Job("other-content", 0, hash: "earlier-content");
        Job("failed", 0, state: "Failed");
        Job("deleted", 0, state: "Deleted");
        Job("enqueued", 0, state: "Enqueued");
        Job("processing", 0, state: "Processing");
        Job("other-dataset", 0, ds: "other");
        Job("other-organization", 0, org: "other");
        Job("other-tool", 0, toolId: "export");
        Job("whole-dataset", 0, path: null, hash: null);
        Job("earlier", 1);
        Job("newest", 2);

        (await KeepLatest()).ShouldBe(1);

        Jobs().ShouldBe(
        [
            "other-path", "other-case", "folder-prefix", "other-content", "failed", "deleted", "enqueued", "processing",
            "other-dataset", "other-organization", "other-tool", "whole-dataset", "newest"
        ], ignoreOrder: true);
    }

    /// <summary>An attempt that runs again under its id (a retry) is no longer an ended one: it is left to its run.</summary>
    [Test]
    public async Task AnActiveNewerJob_IsNotCounted_TheNewestSucceededStays()
    {
        Job("earlier", 0);
        Job("later", 1);
        Job("running", 2, state: "Processing");

        (await KeepLatest()).ShouldBe(1);

        Jobs().ShouldBe(["later", "running"]);
    }

    // ---- the question asked before libddb is: has a build of the entry succeeded before this one? ----

    private Task<bool> HasSeveral(string path = Path) =>
        new JobIndexQuery(_context).HasSeveralSucceededJobsAsync(Org, Ds, HeavyToolIds.Build, path);

    [Test]
    public async Task HasSeveralSucceededJobs_OnlyForTwoSucceededBuildsOfThePath()
    {
        (await HasSeveral()).ShouldBeFalse();

        Job("first", 0);
        Job("failed", 1, state: "Failed");
        Job("running", 2, state: "Processing");
        Job("other-path", 3, path: "site/other.obj");
        Job("other-dataset", 4, ds: "other");
        Job("other-tool", 5, toolId: "export");
        (await HasSeveral()).ShouldBeFalse();

        Job("second", 6, hash: "other-content");
        (await HasSeveral()).ShouldBeTrue();
        (await HasSeveral("site/other.obj")).ShouldBeFalse();
    }
}
