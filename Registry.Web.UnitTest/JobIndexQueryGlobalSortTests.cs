using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-PRC-28: <see cref="JobIndexQuery.QueryGlobalAsync"/> orders the admin task list by the whitelisted field in the
/// asked direction, with a deterministic tie-break (newest first, then job id), and pages the ordered rows.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~JobIndexQueryGlobalSortTests"
/// </summary>
[TestFixture]
public class JobIndexQueryGlobalSortTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private RegistryContext _context = null!;
    private JobIndexQuery _query = null!;

    [SetUp]
    public async Task Setup()
    {
        _context = TestDb.CreateContext<RegistryContext>();
        _query = new JobIndexQuery(_context);

        // id, tool, state, minutes after T0
        Add("j1", "build", "Failed", 10);
        Add("j2", "align", "Succeeded", 20);
        Add("j3", "zip", "Failed", 30);
        Add("j4", "build", "Processing", 40);
        Add("j5", "align", "Failed", 50);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void Add(string id, string tool, string state, int minutes) =>
        _context.JobIndices.Add(new JobIndex
        {
            JobId = id, OrgSlug = "org", DsSlug = "ds", Path = "p", ToolId = tool, CurrentState = state,
            CreatedAtUtc = T0.AddMinutes(minutes)
        });

    private async Task<string[]> IdsAsync(JobIndexGlobalQueryFilter filter) =>
        (await _query.QueryGlobalAsync(filter)).Select(j => j.JobId).ToArray();

    [Test]
    public async Task Default_IsCreatedAtDescending()
    {
        (await IdsAsync(new JobIndexGlobalQueryFilter())).ShouldBe(["j5", "j4", "j3", "j2", "j1"]);
    }

    [Test]
    public async Task CreatedAt_Ascending()
    {
        var filter = new JobIndexGlobalQueryFilter(SortField: JobIndexSortField.CreatedAt, SortOrder: JobIndexSortOrder.Ascending);
        (await IdsAsync(filter)).ShouldBe(["j1", "j2", "j3", "j4", "j5"]);
    }

    [Test]
    public async Task CreatedAt_Descending()
    {
        var filter = new JobIndexGlobalQueryFilter(SortField: JobIndexSortField.CreatedAt, SortOrder: JobIndexSortOrder.Descending);
        (await IdsAsync(filter)).ShouldBe(["j5", "j4", "j3", "j2", "j1"]);
    }

    [Test]
    public async Task State_Ascending_TiesAreNewestFirst()
    {
        var filter = new JobIndexGlobalQueryFilter(SortField: JobIndexSortField.State, SortOrder: JobIndexSortOrder.Ascending);
        // Failed (j5, j3, j1 newest first), Processing, Succeeded
        (await IdsAsync(filter)).ShouldBe(["j5", "j3", "j1", "j4", "j2"]);
    }

    [Test]
    public async Task State_Descending_TiesAreNewestFirst()
    {
        var filter = new JobIndexGlobalQueryFilter(SortField: JobIndexSortField.State, SortOrder: JobIndexSortOrder.Descending);
        (await IdsAsync(filter)).ShouldBe(["j2", "j4", "j5", "j3", "j1"]);
    }

    [Test]
    public async Task ToolId_Ascending_TiesAreNewestFirst()
    {
        var filter = new JobIndexGlobalQueryFilter(SortField: JobIndexSortField.ToolId, SortOrder: JobIndexSortOrder.Ascending);
        // align (j5, j2), build (j4, j1), zip (j3)
        (await IdsAsync(filter)).ShouldBe(["j5", "j2", "j4", "j1", "j3"]);
    }

    [Test]
    public async Task ToolId_Descending_TiesAreNewestFirst()
    {
        var filter = new JobIndexGlobalQueryFilter(SortField: JobIndexSortField.ToolId, SortOrder: JobIndexSortOrder.Descending);
        (await IdsAsync(filter)).ShouldBe(["j3", "j4", "j1", "j5", "j2"]);
    }

    [Test]
    public async Task CreatedAtTies_AreOrderedByJobId_InBothDirections()
    {
        Add("t-b", "build", "Failed", 100);
        Add("t-a", "build", "Failed", 100);
        Add("t-c", "build", "Failed", 100);
        await _context.SaveChangesAsync();

        (await IdsAsync(new JobIndexGlobalQueryFilter(Take: 3))).ShouldBe(["t-a", "t-b", "t-c"]);
        (await IdsAsync(new JobIndexGlobalQueryFilter(Skip: 5, SortOrder: JobIndexSortOrder.Ascending)))
            .ShouldBe(["t-a", "t-b", "t-c"]);
    }

    [Test]
    public async Task Paging_AppliesToTheSortedRows()
    {
        var filter = new JobIndexGlobalQueryFilter(Skip: 1, Take: 2, SortField: JobIndexSortField.ToolId,
            SortOrder: JobIndexSortOrder.Ascending);
        (await IdsAsync(filter)).ShouldBe(["j2", "j4"]);
    }

    [Test]
    public async Task Filters_StillApplyWithAnOrder()
    {
        var filter = new JobIndexGlobalQueryFilter(State: "Failed", SortField: JobIndexSortField.ToolId,
            SortOrder: JobIndexSortOrder.Descending);
        (await IdsAsync(filter)).ShouldBe(["j3", "j1", "j5"]);
        (await _query.CountGlobalAsync(null, "Failed", null)).ShouldBe(3);
    }

    [Test]
    public void AFieldOutsideTheEnum_IsRefused()
    {
        var filter = new JobIndexGlobalQueryFilter(SortField: (JobIndexSortField)99);
        Should.Throw<ArgumentOutOfRangeException>(async () => await _query.QueryGlobalAsync(filter));
    }
}
