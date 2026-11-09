using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="JobIndexQuery.GetActiveOutputsAsync"/> on SQLite: the declared outputs of the unfinished tasks of one
/// dataset (Indexing included), never those of ended tasks, of other datasets or of rows without outputs.
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
