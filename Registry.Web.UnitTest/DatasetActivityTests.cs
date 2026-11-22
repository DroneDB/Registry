using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Which datasets are busy (<see cref="DatasetActivity"/>): a dataset is busy while an import of it or a rescan of its
/// index is queued or running, in every active state of a task; a task that adds one file, a build, and a task that has
/// ended leave it usable. The newest locking task is the one reported.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetActivityTests"
/// </summary>
[TestFixture]
public class DatasetActivityTests
{
    private const string Org = "acme";

    private static RegistryContext Context(params JobIndex[] jobs) => TestDb.CreateContext<RegistryContext>(context =>
    {
        context.JobIndices.AddRange(jobs);
        context.SaveChanges();
    });

    private static JobIndex Job(string id, string tool, string state, string ds = "alpha", string org = Org,
        int? progress = null, int minutesAgo = 0) => new()
    {
        JobId = id, OrgSlug = org, DsSlug = ds, ToolId = tool, CurrentState = state, ProgressPercent = progress,
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };

    private static readonly string[] ActiveStates = TaskStateCatalog.Active;

    [Test]
    public async Task ADatasetIsBusy_ForEachLockingTool_InEachActiveState(
        [Values(HeavyToolIds.ImportDataset, HeavyToolIds.RescanIndex)] string tool,
        [ValueSource(nameof(ActiveStates))] string state)
    {
        await using var context = Context(Job("t1", tool, state, progress: 42));

        var busy = await new DatasetActivity(context).GetAsync(Org, "alpha");

        busy.ShouldBe(new DatasetBusy(
            tool == HeavyToolIds.ImportDataset ? DatasetActivity.Importing : DatasetActivity.Rescanning, "t1", 42));
    }

    [Test]
    public async Task ADatasetIsNotBusy_ForAnotherTool(
        [Values(HeavyToolIds.Build, HeavyToolIds.RescanEntries, "import-file", "archive-extract")] string tool)
    {
        await using var context = Context(Job("t1", tool, "Processing"));

        (await new DatasetActivity(context).GetAsync(Org, "alpha")).ShouldBeNull();
    }

    [Test]
    public async Task ADatasetIsNotBusy_OnceTheTaskEnded([ValueSource(typeof(TaskStateCatalog), nameof(TaskStateCatalog.Terminal))] string state)
    {
        await using var context = Context(Job("t1", HeavyToolIds.ImportDataset, state));

        (await new DatasetActivity(context).GetAsync(Org, "alpha")).ShouldBeNull();
    }

    /// <summary>Another dataset, the same slug in another organization, or a slug of another letter case is not this one.</summary>
    [Test]
    public async Task OnlyTheTasksOfTheDataset_Count()
    {
        await using var context = Context(Job("t1", HeavyToolIds.ImportDataset, "Processing", ds: "beta"),
            Job("t2", HeavyToolIds.ImportDataset, "Processing", org: "other"),
            Job("t3", HeavyToolIds.ImportDataset, "Processing", ds: "ALPHA"));

        (await new DatasetActivity(context).GetAsync(Org, "alpha")).ShouldBeNull();
    }

    [Test]
    public async Task TheNewestLockingTask_IsReported()
    {
        await using var context = Context(Job("old", HeavyToolIds.ImportDataset, "Processing", minutesAgo: 10),
            Job("new", HeavyToolIds.RescanIndex, "Enqueued", minutesAgo: 1));

        var activity = new DatasetActivity(context);

        (await activity.GetAsync(Org, "alpha")).ShouldBe(new DatasetBusy(DatasetActivity.Rescanning, "new", null));
        (await activity.ActiveTaskIdsAsync(Org, "alpha")).ShouldBe(["old", "new"], ignoreOrder: true);
    }

    [Test]
    public async Task TheListQuery_ReturnsTheBusyDatasetsOfThePage_Only()
    {
        await using var context = Context(Job("a", HeavyToolIds.ImportDataset, "Processing", ds: "alpha", progress: 5),
            Job("b-old", HeavyToolIds.ImportDataset, "Processing", ds: "beta", minutesAgo: 5),
            Job("b-new", HeavyToolIds.RescanIndex, "Scheduled", ds: "beta"),
            Job("c", HeavyToolIds.Build, "Processing", ds: "gamma"),
            Job("d", HeavyToolIds.RescanIndex, "Processing", ds: "delta"),
            Job("e", HeavyToolIds.RescanIndex, "Processing", ds: "alpha", org: "other"));

        var busy = await new DatasetActivity(context).GetForOrgAsync(Org, ["alpha", "beta", "gamma", "epsilon"]);

        busy.Count.ShouldBe(2);
        busy["alpha"].ShouldBe(new DatasetBusy(DatasetActivity.Importing, "a", 5));
        busy["beta"].ShouldBe(new DatasetBusy(DatasetActivity.Rescanning, "b-new", null));
        (await new DatasetActivity(context).GetForOrgAsync(Org, [])).ShouldBeEmpty();
    }

    [Test]
    public async Task TheRefusal_NamesTheTask_AndCannotBeRetried()
    {
        await using var context = Context(Job("t1", HeavyToolIds.RescanIndex, "Processing"));

        var ex = await Should.ThrowAsync<DatasetBusyException>(() => new DatasetActivity(context).EnsureNotBusyAsync(Org, "alpha"));

        ex.Message.ShouldBe("Dataset 'acme/alpha' is rescanning: it can only be deleted until the rescan ends");
        ex.Kind.ShouldBe(DatasetActivity.Rescanning);
        ex.TaskId.ShouldBe("t1");
        ex.NoRetry.ShouldBeTrue();
        ex.ShouldBeAssignableTo<ConflictException>();
        await new DatasetActivity(context).EnsureNotBusyAsync(Org, "beta");
    }
}
