using System;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Services.Stac;

namespace Registry.Web.Test;

[TestFixture]
public class StacManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock;
    private ILogger<StacManager> _stacManagerLogger;
    private Mock<IDdbManager> _ddbManagerMock;

    [SetUp]
    public void Setup()
    {
        _authManagerMock = new Mock<IAuthManager>();
        _ddbManagerMock = new Mock<IDdbManager>();

        _stacManagerLogger = CreateTestLogger<StacManager>();
    }

    [Test]
    public async Task GetLandingPage_LinksEveryReadableDatasetWithItsName()
    {
        await using var context = GetTest1Context();
        var (stacManager, _, _) = CreateCollectionsManager(context, names: _ => "Default");

        var landing = await stacManager.GetLandingPage();

        landing.Type.ShouldBe("Catalog");
        landing.StacVersion.ShouldBe("1.1.0");
        landing.Links.ShouldNotBeNull().Single(l => l.Relationship == "self").Href.ShouldBe("http://localhost/stac");
        landing.Links.Single(l => l.Relationship == "root").Href.ShouldBe("http://localhost/stac");
        var child = landing.Links.Single(l => l.Relationship == "child");
        child.Href.ShouldBe("http://localhost/stac/collections/public/default");
        child.Title.ShouldBe("Default");
    }

    [Test]
    public async Task GetCollections_ListsFromLoadedEntities_NoSlugLookup()
    {
        // The listing works on the entities it already loaded: no per-dataset lookup by slug
        await using var context = GetContextWithDatasets("listed");
        var (stacManager, utilsMock, _) = CreateCollectionsManager(context);

        var result = await stacManager.GetCollections();

        result.Collections.ShouldNotBeNull().Cast<JsonObject>().Select(c => (string)c["id"]!)
            .ShouldBe(["public/listed"]);
        utilsMock.Verify(o => o.GetDatasetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GetCollections_OneBrokenIndex_OthersStillListed()
    {
        await using var context = GetContextWithDatasets("good-1", "broken", "good-2");
        var (stacManager, _, _) = CreateCollectionsManager(context, brokenSlug: "broken");

        var result = await stacManager.GetCollections();

        result.Collections.ShouldNotBeNull().Cast<JsonObject>().Select(c => (string)c["id"]!)
            .ShouldBe(["public/good-1", "public/good-2"]);
    }

    [Test]
    public async Task GetCollections_Paged_NextAndPrevLinks()
    {
        await using var context = GetContextWithDatasets("ds-a", "ds-b", "ds-c");
        var (stacManager, _, _) = CreateCollectionsManager(context);

        var first = await stacManager.GetCollections(limit: 2);

        first.Collections.ShouldNotBeNull().Cast<JsonObject>().Select(c => (string)c["id"]!)
            .ShouldBe(["public/ds-a", "public/ds-b"]);
        first.NumberMatched.ShouldBe(3);
        first.NumberReturned.ShouldBe(2);
        first.Links.ShouldNotBeNull().Single(l => l.Relationship == "next").Href
            .ShouldBe("http://localhost/stac/collections?limit=2&offset=2");
        first.Links.ShouldNotContain(l => l.Relationship == "prev");

        var second = await stacManager.GetCollections(limit: 2, offset: 2);

        second.Collections.ShouldNotBeNull().Cast<JsonObject>().Select(c => (string)c["id"]!)
            .ShouldBe(["public/ds-c"]);
        second.Links.ShouldNotBeNull().ShouldNotContain(l => l.Relationship == "next");
        second.Links.Single(l => l.Relationship == "prev").Href
            .ShouldBe("http://localhost/stac/collections?limit=2&offset=0");
    }

    [Test]
    public async Task GetLandingPage_ColdPageCache_DoesNotReopenIndexesForNames()
    {
        await using var context = GetContextWithDatasets("ds-a", "ds-b", "ds-c");
        var (stacManager, _, _) = CreateCollectionsManager(context);

        // Two different users: the per-user landing page cache is cold for the second one
        _authManagerMock.SetupSequence(o => o.SafeGetCurrentUserName())
            .ReturnsAsync("user-1").ReturnsAsync("user-2");

        var first = await stacManager.GetLandingPage();
        _ddbManagerMock.Invocations.Clear();
        var second = await stacManager.GetLandingPage();

        _ddbManagerMock.Verify(o => o.Get(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        second.Links.ShouldNotBeNull().Where(l => l.Relationship == "child").Select(l => l.Title)
            .ShouldBe(first.Links.ShouldNotBeNull().Where(l => l.Relationship == "child").Select(l => l.Title));
        second.Links.Where(l => l.Relationship == "child").Select(l => l.Title)
            .ShouldBe(["Name ds-a", "Name ds-b", "Name ds-c"]);
    }

    private (StacManager manager, Mock<IDatasetResolver> utils, MemoryDistributedCache cache) CreateCollectionsManager(
        RegistryContext context, string? brokenSlug = null, Func<string, string>? names = null)
    {
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), AccessType.Read)).ReturnsAsync(true);
        _authManagerMock.Setup(o => o.CanListDataset(It.IsAny<Dataset>())).ReturnsAsync(true);
        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).ReturnsAsync("user");
        names ??= slug => "Name " + slug;

        var utilsMock = new Mock<IDatasetResolver>();
        var urlsMock = new Mock<IUrlBuilder>();
        urlsMock.Setup(o => o.GetLocalHost(It.IsAny<bool>())).Returns("http://localhost");
        urlsMock.Setup(o => o.GenerateDatasetUrl(It.IsAny<Dataset>(), It.IsAny<bool>()))
            .Returns((Dataset d, bool _) => $"http://localhost/orgs/public/ds/{d.Slug}");
        utilsMock.Setup(o => o.GetDatasetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("GetDataset must not be called"));

        foreach (var ds in context.Datasets.Include(d => d.Organization).ToArray())
        {
            var metaMock = new Mock<Registry.Ports.DroneDB.IMetaManager>();
            metaMock.Setup(o => o.Get<string>(SafeMetaManager.NameField, null)).Returns(names(ds.Slug));

            var ddbMock = new Mock<IDDB>();
            ddbMock.Setup(o => o.Meta).Returns(metaMock.Object);
            ddbMock.Setup(o => o.DatasetFolderPath).Returns("/datasets/public/" + ds.InternalRef);
            var slug = ds.Slug;
            if (slug == brokenSlug)
                ddbMock.Setup(o => o.GetStacData(null)).Throws(new InvalidOperationException("corrupt index"));
            else
                ddbMock.Setup(o => o.GetStacData(null)).Returns(() => TestJson.Parse("""{"type":"Collection"}"""));

            _ddbManagerMock.Setup(o => o.Get(ds.Organization.Slug, ds.InternalRef)).Returns(ddbMock.Object);
        }

        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var catalog = new StacCatalog(context, _authManagerMock.Object, TestCaches.WithDatasetVisibility(cache),
            _ddbManagerMock.Object, TestCaches.Records(cache), TestCaches.Generations(cache));

        return (new StacManager(
            new DatasetAccessResolver(utilsMock.Object, _authManagerMock.Object, _ddbManagerMock.Object, TestPaths.Policy),
            _ddbManagerMock.Object, catalog, new StacUrls(urlsMock.Object), _stacManagerLogger), utilsMock, cache);
    }

    private static RegistryContext GetContextWithDatasets(params string[] slugs)
    {
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            var organization = Seed.Organization(context, MagicStrings.PublicOrganizationSlug, "Public", isPublic: true);
            foreach (var slug in slugs)
                Seed.Dataset(context, organization, slug);
            context.SaveChanges();
        });
    }

    #region TestContexts

    private static RegistryContext GetTest1Context()
    {
        // Insert seed data into the database using one instance of the context
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            Seed.PublicDefaultDataset(context);

            context.SaveChanges();
        });
    }

    #endregion
}