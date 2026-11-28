using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-DEV-14: the total of a page of the datasets of an organization (<c>X-Total-Count</c>) counts the datasets that
/// match every filter of the page (<c>slugs</c>, <c>q</c>), only without the paging.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetListPageTotalTests"
/// </summary>
[TestFixture]
public class DatasetListPageTotalTests
{
    private const string Org = DatasetVisibilityWorld.PublicOrg;

    private DatasetVisibilityWorld _world = null!;

    [SetUp]
    public void Setup() => _world = new DatasetVisibilityWorld();

    [TearDown]
    public void TearDown() => _world.Dispose();

    private async Task<(int Items, int Total)> PageAsync(DatasetListQuery query)
    {
        var (items, total) = await _world.Datasets(_world.Owner).ListPage(Org, query);
        return (items.Count, total);
    }

    [Test]
    public async Task NoFilter_CountsEveryDataset() =>
        (await PageAsync(new DatasetListQuery(Take: 1))).ShouldBe((1, 3));

    [Test]
    public async Task Slugs_CountOnlyTheNamedDatasets()
    {
        (await PageAsync(new DatasetListQuery(Slugs: ["a-public", "b-unlisted"]))).ShouldBe((2, 2));
        (await PageAsync(new DatasetListQuery(Slugs: ["a-public"]))).ShouldBe((1, 1));
        (await PageAsync(new DatasetListQuery(Take: 1, Slugs: ["a-public", "b-unlisted"]))).ShouldBe((1, 2));
    }

    [Test]
    public async Task Query_CountsTheMatchingDatasets() =>
        (await PageAsync(new DatasetListQuery(Query: "unlisted"))).ShouldBe((1, 1));
}
