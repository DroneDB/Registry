using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="JobIndexQuery.GetActiveOutputsAsync"/> on SQLite: the declared outputs of the unfinished tasks of one
/// dataset (Indexing included), never those of ended tasks, of other datasets or of rows without outputs; and
/// <see cref="JobIndexQuery.FindActiveDedupCandidateAsync"/>, the deduplication of a tool that writes the dataset.
/// </summary>
[TestFixture]
public class JobIndexActiveOutputsTests
{
    private static JobIndex Row(string id, string state, string? outputs, string ds = "ds") => new()
    {
        JobId = id, OrgSlug = "org", DsSlug = ds, CurrentState = state, ToolId = "merge-multispectral",
        CreatedAtUtc = DateTime.UtcNow, LastStateChangeUtc = DateTime.UtcNow, OutputPathsJson = outputs
    };

    [Test]
    public async Task ReturnsTheOutputsOfTheUnfinishedTasksOfTheDataset()
    {
        await using var db = TestDb.CreateContext<RegistryContext>(ctx =>
        {
            ctx.JobIndices.AddRange(
                Row("indexing", "Indexing", "[\"a.tif\"]"),
                Row("processing", "Processing", "[\"b.tif\",\"c.tif\"]"),
                Row("succeeded", "Succeeded", "[\"d.tif\"]"),
                Row("failed", "Failed", "[\"e.tif\"]"),
                Row("other-ds", "Enqueued", "[\"f.tif\"]", ds: "other"),
                Row("no-outputs", "Enqueued", null),
                Row("not-json", "Enqueued", "a.tif"));
            ctx.SaveChanges();
        });

        var active = await new JobIndexQuery(db).GetActiveOutputsAsync("org", "ds");

        active.OrderBy(a => a.JobId).Select(a => (a.JobId, string.Join(",", a.Paths)))
            .ShouldBe([("indexing", "a.tif"), ("processing", "b.tif,c.tif")]);
        active.ShouldAllBe(a => a.ToolId == "merge-multispectral");
    }

    [Test]
    public async Task ActiveDedupCandidate_IsTheNewestUnfinishedTaskOfTheSameRequest_NeverAFinishedOne()
    {
        JobIndex Same(string id, string state, int minutesAgo, string hash = "h", string ds = "ds")
        {
            var row = Row(id, state, "[\"out.tif\"]", ds);
            row.RequestHash = hash;
            row.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo);
            return row;
        }

        await using var db = TestDb.CreateContext<RegistryContext>(ctx =>
        {
            ctx.JobIndices.AddRange(
                Same("succeeded", "Succeeded", 1),
                Same("failed", "Failed", 2),
                Same("other-hash", "Processing", 1, hash: "x"),
                Same("other-ds", "Processing", 1, ds: "other"));
            ctx.SaveChanges();
        });
        var query = new JobIndexQuery(db);

        // A succeeded merge whose output was since deleted: the same request is not a duplicate of it
        (await query.FindActiveDedupCandidateAsync("org", "ds", "merge-multispectral", "h")).ShouldBeNull();
        (await query.FindDedupCandidateAsync("org", "ds", "merge-multispectral", "h", 24))!.JobId.ShouldBe("succeeded");

        db.JobIndices.AddRange(Same("old-active", "Enqueued", 30), Same("indexing", "Indexing", 5));
        await db.SaveChangesAsync();

        (await query.FindActiveDedupCandidateAsync("org", "ds", "merge-multispectral", "h"))!.JobId.ShouldBe("indexing");
    }

    // FR-RAS-11: the task of a read tool is authorized per user, so a submit reuses only a task of the same user (an
    // anonymous one, only a task of no user)
    [Test]
    public async Task DedupCandidates_OfOneOwner_AreOnlyTheTasksOfThatUser()
    {
        JobIndex Of(string id, string? user, int minutesAgo)
        {
            var row = Row(id, "Processing", null);
            row.ToolId = "raster-contours";
            row.RequestHash = "h";
            row.UserId = user;
            row.CreatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo);
            return row;
        }

        await using var db = TestDb.CreateContext<RegistryContext>(ctx =>
        {
            ctx.JobIndices.AddRange(Of("of-u1", "u1", 3), Of("of-u2", "u2", 2), Of("of-nobody", null, 1));
            ctx.SaveChanges();
        });
        var query = new JobIndexQuery(db);

        (await query.FindDedupCandidateAsync("org", "ds", "raster-contours", "h", 24, new TaskOwnerScope("u1")))!
            .JobId.ShouldBe("of-u1");
        (await query.FindActiveDedupCandidateAsync("org", "ds", "raster-contours", "h", new TaskOwnerScope("u2")))!
            .JobId.ShouldBe("of-u2");
        (await query.FindDedupCandidateAsync("org", "ds", "raster-contours", "h", 24, new TaskOwnerScope(null)))!
            .JobId.ShouldBe("of-nobody");
        (await query.FindDedupCandidateAsync("org", "ds", "raster-contours", "h", 24, new TaskOwnerScope("u3")))
            .ShouldBeNull();
        (await query.FindDedupCandidateAsync("org", "ds", "raster-contours", "h", 24))!.JobId.ShouldBe("of-nobody",
            "without an owner, the newest task of anybody");
    }

    [Test]
    public async Task ATaskRecordWithoutOutputPaths_WasQueuedBeforeTheOverwriteRule()
    {
        await using var db = TestDb.CreateContext<RegistryContext>(ctx =>
        {
            ctx.JobIndices.AddRange(Row("old", "Enqueued", null), Row("new", "Enqueued", "[\"a.tif\"]"));
            ctx.SaveChanges();
        });

        (await HeavyTaskJobWrapper.QueuedBeforeOverwriteRuleAsync(db, "old", default)).ShouldBeTrue();
        (await HeavyTaskJobWrapper.QueuedBeforeOverwriteRuleAsync(db, "new", default)).ShouldBeFalse();
        (await HeavyTaskJobWrapper.QueuedBeforeOverwriteRuleAsync(db, "missing", default)).ShouldBeFalse();
    }
}
