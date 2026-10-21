using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
public class StacManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IDatasetsManager> _datasetManagerMock;
    private ILogger<StacManager> _stacManagerLogger;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IDdbManager> _ddbManagerMock;

    [SetUp]
    public void Setup()
    {
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _authManagerMock = new Mock<IAuthManager>();
        _datasetManagerMock = new Mock<IDatasetsManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _ddbManagerMock = new Mock<IDdbManager>();

        _stacManagerLogger = CreateTestLogger<StacManager>();
    }

    [Test]
    public async Task GetCatalog_Ok()
    {
        await using var context = GetTest1Context();

        const string stacUrl = "http://localhost/orgs/public/ds/default/stac";
        const string datasetStacUrl = "http://localhost/orgs/public/ds/default/stac/catalog.json";
        const string expectedName = "Default";

        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        var utilsMock = new Mock<IUtils>();
        utilsMock.Setup(o => o.GenerateStacUrl()).Returns(stacUrl);
        utilsMock.Setup(o => o.GenerateDatasetStacUrl(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(datasetStacUrl);

        var metaMock = new Mock<Registry.Ports.DroneDB.IMetaManager>();
        metaMock.Setup(o => o.Get<int>(SafeMetaManager.VisibilityField, null))
            .Returns((int)Visibility.Public);
        metaMock.Setup(o => o.Get<string>(SafeMetaManager.NameField, null))
            .Returns(expectedName);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(o => o.Meta).Returns(metaMock.Object);

        _ddbManagerMock.Setup(o => o.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

        var stacManager = new StacManager(_authManagerMock.Object, context, utilsMock.Object,
            _ddbManagerMock.Object, cache, _stacManagerLogger, TestCaches.WithDatasetVisibility(cache));

        var catalog = await stacManager.GetCatalog();

        catalog.ShouldNotBeNull();
        catalog.Type.ShouldBe("Catalog");
        catalog.StacVersion.ShouldBe("1.1.0");

        var selfLink = catalog.Links.Single(l => l.Relationship == "self");
        selfLink.Href.ShouldBe(stacUrl);

        var rootLink = catalog.Links.Single(l => l.Relationship == "root");
        rootLink.Href.ShouldBe(stacUrl);

        var childLink = catalog.Links.Single(l => l.Relationship == "child");
        childLink.Href.ShouldBe(datasetStacUrl);
        childLink.Title.ShouldBe(expectedName);
    }

    [Test]
    public async Task GetCollections_ListsFromLoadedEntities_NoSlugLookup()
    {
        // The listing works on the entities it already loaded: no per-dataset lookup by slug
        await using var context = GetContextWithDatasets("listed");
        var (stacManager, utilsMock, _) = CreateCollectionsManager(context);

        var result = await stacManager.GetCollections();

        result.Collections.Cast<JObject>().Select(c => c["id"]!.Value<string>())
            .ShouldBe(["public/listed"]);
        utilsMock.Verify(o => o.GetDataset(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task GetCollections_OneBrokenIndex_OthersStillListed()
    {
        await using var context = GetContextWithDatasets("good-1", "broken", "good-2");
        var (stacManager, _, _) = CreateCollectionsManager(context, brokenSlug: "broken");

        var result = await stacManager.GetCollections();

        result.Collections.Cast<JObject>().Select(c => c["id"]!.Value<string>())
            .ShouldBe(["public/good-1", "public/good-2"]);
    }

    [Test]
    public async Task GetCollections_Paged_NextAndPrevLinks()
    {
        await using var context = GetContextWithDatasets("ds-a", "ds-b", "ds-c");
        var (stacManager, _, _) = CreateCollectionsManager(context);

        var first = await stacManager.GetCollections(limit: 2);

        first.Collections.Cast<JObject>().Select(c => c["id"]!.Value<string>())
            .ShouldBe(["public/ds-a", "public/ds-b"]);
        first.NumberMatched.ShouldBe(3);
        first.NumberReturned.ShouldBe(2);
        first.Links.Single(l => l.Relationship == "next").Href
            .ShouldBe("http://localhost/stac/collections?limit=2&offset=2");
        first.Links.ShouldNotContain(l => l.Relationship == "prev");

        var second = await stacManager.GetCollections(limit: 2, offset: 2);

        second.Collections.Cast<JObject>().Select(c => c["id"]!.Value<string>())
            .ShouldBe(["public/ds-c"]);
        second.Links.ShouldNotContain(l => l.Relationship == "next");
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
        second.Links.Where(l => l.Relationship == "child").Select(l => l.Title)
            .ShouldBe(first.Links.Where(l => l.Relationship == "child").Select(l => l.Title));
        second.Links.Where(l => l.Relationship == "child").Select(l => l.Title)
            .ShouldBe(["Name ds-a", "Name ds-b", "Name ds-c"]);
    }

    private (StacManager manager, Mock<IUtils> utils, MemoryDistributedCache cache) CreateCollectionsManager(
        RegistryContext context, string brokenSlug = null)
    {
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), AccessType.Read)).ReturnsAsync(true);
        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).ReturnsAsync("user");

        var utilsMock = new Mock<IUtils>();
        utilsMock.Setup(o => o.GetLocalHost(It.IsAny<bool>())).Returns("http://localhost");
        utilsMock.Setup(o => o.GenerateDatasetUrl(It.IsAny<Dataset>(), It.IsAny<bool>()))
            .Returns((Dataset d, bool _) => $"http://localhost/orgs/public/ds/{d.Slug}");
        utilsMock.Setup(o => o.GetDataset(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Throws(new InvalidOperationException("GetDataset must not be called"));

        foreach (var ds in context.Datasets.Include(d => d.Organization).ToArray())
        {
            var metaMock = new Mock<Registry.Ports.DroneDB.IMetaManager>();
            metaMock.Setup(o => o.Get<string>(SafeMetaManager.NameField, null)).Returns("Name " + ds.Slug);

            var ddbMock = new Mock<IDDB>();
            ddbMock.Setup(o => o.Meta).Returns(metaMock.Object);
            var slug = ds.Slug;
            if (slug == brokenSlug)
                ddbMock.Setup(o => o.GetStac(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null))
                    .Throws(new InvalidOperationException("corrupt index"));
            else
                ddbMock.Setup(o => o.GetStac(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null))
                    .Returns((string id, string _, string _, string _) =>
                        new JObject { ["id"] = id, ["type"] = "Collection" });

            _ddbManagerMock.Setup(o => o.Get(ds.Organization.Slug, ds.InternalRef)).Returns(ddbMock.Object);
        }

        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

        return (new StacManager(_authManagerMock.Object, context, utilsMock.Object, _ddbManagerMock.Object,
            cache, _stacManagerLogger, TestCaches.WithDatasetVisibility(cache)), utilsMock, cache);
    }

    private static RegistryContext GetContextWithDatasets(params string[] slugs)
    {
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            context.Organizations.Add(new Organization
            {
                Slug = MagicStrings.PublicOrganizationSlug,
                Name = "Public",
                CreationDate = DateTime.Now,
                IsPublic = true,
                Datasets = slugs.Select(s => new Dataset
                {
                    Slug = s, CreationDate = DateTime.Now, InternalRef = Guid.NewGuid()
                }).ToList()
            });
            context.SaveChanges();
        });
    }


    #region Test Data

    private readonly AppSettings _settings = JsonConvert.DeserializeObject<AppSettings>(@"{
    ""Secret"": ""a2780070a24cfcaf5a4a43f931200ba0d19d8b86b3a7bd5123d9ad75b125f480fcce1f9b7f41a53abe2ba8456bd142d38c455302e0081e5139bc3fc9bf614497"",
    ""TokenExpirationInDays"": 7,
    ""RevokedTokens"": [
      """"
    ],
    ""AuthProvider"": ""Sqlite"",
    ""RegistryProvider"": ""Sqlite"",
    ""StorageProvider"": {
      ""type"": ""Physical"",
      ""settings"": {
        ""path"": ""./temp""
      }
    },
    ""DefaultAdmin"": {
      ""Email"": ""admin@example.com"",
      ""UserName"": ""admin"",
      ""Password"": ""password""
    },
    ""DdbStoragePath"": ""./Data/Ddb"",
    ""TempPath"": ""./temp"",
    ""DdbPath"": ""./ddb""}
  ");

    #endregion

    #region TestContexts

    private static RegistryContext GetTest1Context()
    {
        // Insert seed data into the database using one instance of the context
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            var entity = new Organization
            {
                Slug = MagicStrings.PublicOrganizationSlug,
                Name = "Public",
                CreationDate = DateTime.Now,
                Description = "Public organization",
                IsPublic = true,
                OwnerId = null
            };
            var ds = new Dataset
            {
                Slug = MagicStrings.DefaultDatasetSlug,
                //Name = "Default",
                //IsPublic = true,
                CreationDate = DateTime.Now,
                //LastUpdate = DateTime.Now,
                InternalRef = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75")
            };
            entity.Datasets = new List<Dataset> { ds };

            context.Organizations.Add(entity);

            context.SaveChanges();
        });
    }

    private static ApplicationDbContext GetAppTest1Context()
    {
        // Insert seed data into the database using one instance of the context
        return TestDb.CreateContext<ApplicationDbContext>(context =>
        {
            var adminRole = new IdentityRole
            {
                Id = "1db5b539-6e54-4674-bb74-84732eb48204",
                Name = "admin",
                NormalizedName = "ADMIN",
                ConcurrencyStamp = "72c80593-64a2-40b4-b0c4-26a9dcc06400"
            };

            context.Roles.Add(adminRole);

            var standardRole = new IdentityRole
            {
                Id = "7d02507e-8eab-48c0-ba19-fea3ae644ab9",
                Name = "standard",
                NormalizedName = "STANDARD",
                ConcurrencyStamp = "2e279a3c-4273-4f0a-abf6-8e97811651a9"
            };

            context.Roles.Add(standardRole);

            var admin = new User
            {
                Id = "bfb579ce-8435-4c70-a365-158a3d93811f",
                UserName = "admin",
                Email = "admin@example.com",
                NormalizedUserName = "ADMIN"
            };

            context.Users.Add(admin);

            context.UserRoles.Add(new IdentityUserRole<string>
            {
                RoleId = "1db5b539-6e54-4674-bb74-84732eb48204",
                UserId = "bfb579ce-8435-4c70-a365-158a3d93811f"
            });

            context.SaveChanges();
        });
    }

    #endregion
}