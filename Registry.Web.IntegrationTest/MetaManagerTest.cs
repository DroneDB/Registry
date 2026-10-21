using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using MetaManager = Registry.Web.Services.Managers.MetaManager;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
internal class MetaManagerTest : TestBase
{
    private ILogger<DdbManager> _ddbFactoryLogger;
    private ILogger<MetaManager> _metaManagerLogger;
    private MutableOptions<AppSettings> _settingsOptions;
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private ICacheManager _cacheManager;

    private const string TestStorageFolder = @"Data/Storage";
    private const string DdbTestDataFolder = @"Data/DdbTest";
    private const string StorageFolder = "Storage";
    private const string DdbFolder = "Ddb";

    private const string BaseTestFolder = "ObjectManagerTest";

    private static readonly string Test4ArchiveUrl = TestData.Url("registry/Test4-new.zip");
    private static readonly string Test5ArchiveUrl = TestData.Url("registry/Test5-new.zip");

    private readonly Guid _defaultDatasetGuid = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75");

    private static readonly IDdbWrapper DdbWrapper = new NativeDdbWrapper();

    [SetUp]
    public void Setup()
    {
        _settingsOptions = TestSettings.Mutable();
        _authManagerMock = new Mock<IAuthManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _cacheManager = TestCaches.InMemoryWithDatasetVisibility();

        _ddbFactoryLogger = CreateTestLogger<DdbManager>();
        _metaManagerLogger = CreateTestLogger<MetaManager>();
    }

    [Test]
    public async Task List_Empty_NoMeta()
    {
        await using var context = GetTest1Context();

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

        var settings = TestSettings.Default;

        settings.DatasetsPath = Path.Combine(test.TestFolder, DdbFolder);
        _settingsOptions.Value = settings;
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var ddbManager = new DdbManager(_settingsOptions, _ddbFactoryLogger, DdbWrapper);

        var webUtils = new WebUtils(_authManagerMock.Object, context, _settingsOptions,
            _httpContextAccessorMock.Object, TestCaches.Summaries(ddbManager));

        var metaManager = new MetaManager(_metaManagerLogger, ddbManager,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, ddbManager), _cacheManager);

        var res = await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug);

        res.ShouldBeEmpty();
    }

    [Test]
    public async Task AddListRemove_HappyPath_Ok()
    {
        await using var context = GetTest1Context();

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

        var settings = TestSettings.Default;

        settings.DatasetsPath = Path.Combine(test.TestFolder, DdbFolder);
        _settingsOptions.Value = settings;
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var ddbManager = new DdbManager(_settingsOptions, _ddbFactoryLogger, DdbWrapper);

        var webUtils = new WebUtils(_authManagerMock.Object, context, _settingsOptions,
            _httpContextAccessorMock.Object, TestCaches.Summaries(ddbManager));

        var metaManager = new MetaManager(_metaManagerLogger, ddbManager,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, ddbManager), _cacheManager);

        var a = await metaManager.Add(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
            "annotations", "{\"test\":123}");

        ((JToken)a.Data)["test"].ToObject<int>().ShouldBe(123);

        var res = await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug);

        res.Count().ShouldBe(1);
        res.First().Count.ShouldBe(1);
        res.First().Key.ShouldBe("annotations");

        var a2 = await metaManager.Add(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
            "annotations",
            "{\"test\":4124,\"pippo\":\"ciao\"}");

        ((JToken)a2.Data)["test"].ToObject<int>().ShouldBe(4124);
        ((JToken)a2.Data)["pippo"].ToObject<string>().ShouldBe("ciao");

        (await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).Count()
            .ShouldBe(1);

        (await metaManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "annotations"))
            .Count().ShouldBe(2);

        (await metaManager.Remove(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, a.Id)).ShouldBe(1);

        (await metaManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "annotations"))
            .Count().ShouldBe(1);

        (await metaManager.Unset(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "annotations"))
            .ShouldBe(1);

        (await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).ShouldBeEmpty();
    }

    #region TestContexts

    private static RegistryContext GetTest1Context()
    {
        // Insert seed data into the database using one instance of the context
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            Seed.PublicDefaultDataset(context);

            var admin = Seed.Organization(context, "admin", description: "Admin", isPublic: true);
            Seed.Dataset(context, admin, "7kd0gxti9qoemsrk", Guid.Parse("6c1f5555-d001-4411-9308-42aa6ccd7fd6"));

            context.SaveChanges();
        });
    }

    #endregion
}
