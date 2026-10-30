using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// An unlisted dataset is read by anyone with its link and listed to nobody else: the list of the datasets of an
/// organization and the STAC catalog (its landing page, its collections, a search over every collection) leave it out
/// for a caller who is not the owner of its organization, a member of it or an administrator, while its own URLs (its
/// STAC collection, its items, a search that names it) keep answering everyone. Over the real access rules.
/// </summary>
[TestFixture]
public class DatasetListingVisibilityTests
{
    private const string PublicOrg = DatasetVisibilityWorld.PublicOrg;
    private const string PrivateOrg = DatasetVisibilityWorld.PrivateOrg;
    private const string Unlisted = PublicOrg + "/b-unlisted";

    private DatasetVisibilityWorld _world = null!;

    [SetUp]
    public void Setup() => _world = new DatasetVisibilityWorld();

    [TearDown]
    public void TearDown() => _world.Dispose();

    private async Task<string[]> OrganizationList(User? caller, string orgSlug) =>
        (await _world.Datasets(caller).List(orgSlug)).Select(d => d.Slug!).ToArray();

    // The collections the STAC landing page links, as "org/dataset"
    private async Task<string[]> LandingPage(User? caller) =>
        (await _world.Stac(caller).GetLandingPage()).Links!.Where(l => l.Relationship == "child")
        .Select(l => l.Href!.Split("/stac/collections/")[1]).ToArray();

    private async Task<string[]> Collections(User? caller) =>
        (await _world.Stac(caller).GetCollections()).Collections!.Cast<JsonObject>()
        .Select(c => (string)c["id"]!).ToArray();

    private static string[] CollectionsOf(JsonNode search) =>
        search["features"]!.AsArray().Select(f => (string)f!["collection"]!).ToArray();

    [Test]
    public async Task TheDatasetListOfAnOrganization_ShowsAnonymousCallersItsPublicDatasetsOnly()
    {
        (await OrganizationList(null, PublicOrg)).ShouldBe(["a-public"], ignoreOrder: true);
    }

    [Test]
    public async Task TheDatasetListOfAnOrganization_ShowsItsOwnerMembersAndAdministratorsTheUnlistedDatasets()
    {
        foreach (var caller in new[] { _world.Owner, _world.Member, _world.Administrator })
        {
            (await OrganizationList(caller, PublicOrg)).ShouldBe(["a-public", "b-unlisted", "c-private"],
                ignoreOrder: true, customMessage: caller.UserName);
            (await OrganizationList(caller, PrivateOrg)).ShouldBe(["a-public", "b-unlisted", "c-private"],
                ignoreOrder: true, customMessage: caller.UserName);
        }
    }

    [Test]
    public async Task TheStacLandingPageAndCollections_ShowAnonymousCallersAndOutsidersThePublicDatasetsOnly()
    {
        string[] publicOnes = [PrivateOrg + "/a-public", PublicOrg + "/a-public"];

        foreach (var caller in new[] { null, _world.Outsider })
        {
            (await LandingPage(caller)).ShouldBe(publicOnes, ignoreOrder: true, customMessage: caller?.UserName);
            (await Collections(caller)).ShouldBe(publicOnes, ignoreOrder: true, customMessage: caller?.UserName);
            (await _world.Stac(caller).GetCollections()).NumberMatched.ShouldBe(publicOnes.Length);
        }
    }

    [Test]
    public async Task TheStacLandingPageAndCollections_ShowAMemberTheUnlistedDatasetsOfTheOrganization()
    {
        (await LandingPage(_world.Member)).ShouldContain(Unlisted);
        (await Collections(_world.Member)).ShouldContain(Unlisted);
        (await LandingPage(_world.Owner)).ShouldContain(Unlisted);
        (await LandingPage(_world.Administrator)).ShouldContain(Unlisted);
    }

    [Test]
    public async Task AStacSearchOverEveryCollection_LeavesTheUnlistedDatasetsOutForAnonymousCallers()
    {
        var search = await _world.Stac(null).Search(new StacSearchRequestDto { Limit = 100 });

        CollectionsOf(search).ShouldBe([PrivateOrg + "/a-public", PublicOrg + "/a-public"], ignoreOrder: true);
    }

    [Test]
    public async Task AStacSearchThatNamesAnUnlistedCollection_AnswersAnonymousCallers()
    {
        var search = await _world.Stac(null).Search(new StacSearchRequestDto { Collections = [Unlisted] });

        CollectionsOf(search).ShouldBe([Unlisted]);
    }

    [Test]
    public async Task AnUnlistedDataset_IsStillReadByItsLink_ItsStacCollectionAndItsItemsToo()
    {
        var dataset = _world.Context.Datasets.Single(d => d.InternalRef == _world.Refs[Unlisted]);
        (await _world.Auth(null).RequestAccess(dataset, AccessType.Read)).ShouldBeTrue();

        var stac = _world.Stac(null);
        ((string)(await stac.GetCollection(PublicOrg, "b-unlisted"))["id"]!).ShouldBe(Unlisted);
        CollectionsOf(await stac.GetCollectionItems(PublicOrg, "b-unlisted")).ShouldBe([Unlisted]);
    }

    [Test]
    public async Task ThePrivateDatasets_AreNeitherListedNorReadByAnonymousCallers()
    {
        var dataset = _world.Context.Datasets.Single(d => d.InternalRef == _world.Refs[PublicOrg + "/c-private"]);
        (await _world.Auth(null).RequestAccess(dataset, AccessType.Read)).ShouldBeFalse();
        (await _world.Auth(null).CanListDataset(dataset)).ShouldBeFalse();
    }
}
