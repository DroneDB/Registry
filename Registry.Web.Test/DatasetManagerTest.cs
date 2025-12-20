using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Models.DTO;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

[TestFixture]
public class DatasetManagerTest : TestBase
{

    private Mock<IAuthManager> _authManagerMock;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IDatasetsManager> _datasetManagerMock;
    private ILogger<DatasetsManager> _datasetsManagerLogger;
    private Mock<IDdbManager> _ddbFactoryMock;
    private Mock<IObjectsManager> _objectsManagerMock;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IStacManager> _stacManagerMock;
    private ICacheManager _cacheManager;

    [SetUp]
    public void Setup()
    {
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _authManagerMock = new Mock<IAuthManager>();
        _datasetManagerMock = new Mock<IDatasetsManager>();
        _ddbFactoryMock = new Mock<IDdbManager>();
        _objectsManagerMock = new Mock<IObjectsManager>();
        _stacManagerMock = new Mock<IStacManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _cacheManager = CreateTestCacheManager();
        RegisterDatasetVisibilityCacheProvider(_cacheManager);
        _datasetsManagerLogger = CreateTestLogger<DatasetsManager>();
    }

    [Test]
    public async Task List_Default_Ok()
    {
        const string expectedSlug = MagicStrings.DefaultDatasetSlug;
        const string expectedName = "Default";


        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object, _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry{
                Properties = new Dictionary<string, object>
                {
                    {"public", true },
                    {"name", expectedName}
                },
                Size = 1000,
                ModifiedTime = DateTime.Now
            }
        );

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object, _cacheManager);

        var list = (await datasetsManager.List(MagicStrings.PublicOrganizationSlug)).ToArray();

        list.Count().ShouldBe(1);

        var pub = list.First();

        pub.Slug.ShouldBe(expectedSlug);

        // TODO: Check test data: this should be true
        pub.Properties["public"].ShouldBe(true);
        pub.Properties["name"].ShouldBe(expectedName);


    }

    [Test]
    public async Task Get_WithPermissions_PopulatesPermissionsProperty()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;
        const string dsSlug = MagicStrings.DefaultDatasetSlug;

        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to return specific permissions
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), AccessType.Read))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = true,
                CanWrite = false,
                CanDelete = false
            }));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = new Dictionary<string, object>
            {
                {"public", true},
                {"name", "Default"}
            },
            Size = 1000,
            ModifiedTime = DateTime.Now
        });

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager);

        // Act
        var result = await datasetsManager.Get(orgSlug, dsSlug);

        // Assert
        result.ShouldNotBeNull();
        result.Permissions.ShouldNotBeNull();
        result.Permissions.CanRead.ShouldBeTrue();
        result.Permissions.CanWrite.ShouldBeFalse();
        result.Permissions.CanDelete.ShouldBeFalse();

        // Verify GetDatasetPermissions was called
        _authManagerMock.Verify(x => x.GetDatasetPermissions(It.IsAny<Dataset>()), Times.Once);
    }

    [Test]
    public async Task Get_WithFullAccess_PopulatesAllPermissions()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;
        const string dsSlug = MagicStrings.DefaultDatasetSlug;

        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to return full access
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), AccessType.Read))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = true,
                CanWrite = true,
                CanDelete = true
            }));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = new Dictionary<string, object>
            {
                {"public", true},
                {"name", "Default"}
            },
            Size = 1000,
            ModifiedTime = DateTime.Now
        });

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager);

        // Act
        var result = await datasetsManager.Get(orgSlug, dsSlug);

        // Assert
        result.ShouldNotBeNull();
        result.Permissions.ShouldNotBeNull();
        result.Permissions.CanRead.ShouldBeTrue();
        result.Permissions.CanWrite.ShouldBeTrue();
        result.Permissions.CanDelete.ShouldBeTrue();
    }

    [Test]
    public async Task GetEntry_IncludesPermissionsInProperties()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;
        const string dsSlug = MagicStrings.DefaultDatasetSlug;

        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to return specific permissions
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), AccessType.Read))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = true,
                CanWrite = true,
                CanDelete = false
            }));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = new Dictionary<string, object>
            {
                {"public", true},
                {"name", "Default"}
            },
            Size = 1000,
            ModifiedTime = DateTime.Now,
            Path = "."
        });

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager);

        // Act
        var result = await datasetsManager.GetEntry(orgSlug, dsSlug);

        // Assert
        result.ShouldNotBeNull();
        result.Count().ShouldBe(1);

        var entry = result.First();
        entry.Properties.ShouldNotBeNull();
        entry.Properties.ShouldContainKey("permissions");

        var permissions = entry.Properties["permissions"];
        permissions.ShouldNotBeNull();

        // Check permissions using reflection (since it's an anonymous object)
        var permissionsDict = JsonConvert.DeserializeObject<Dictionary<string, bool>>(
            JsonConvert.SerializeObject(permissions));

        permissionsDict.ShouldContainKey("canRead");
        permissionsDict.ShouldContainKey("canWrite");
        permissionsDict.ShouldContainKey("canDelete");
        permissionsDict["canRead"].ShouldBeTrue();
        permissionsDict["canWrite"].ShouldBeTrue();
        permissionsDict["canDelete"].ShouldBeFalse();

        // Verify GetDatasetPermissions was called
        _authManagerMock.Verify(x => x.GetDatasetPermissions(It.IsAny<Dataset>()), Times.Once);
    }

    [Test]
    public async Task GetEntry_WithNoAccess_IncludesPermissionsAsFalse()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;
        const string dsSlug = MagicStrings.DefaultDatasetSlug;

        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to return read-only access
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), AccessType.Read))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = true,
                CanWrite = false,
                CanDelete = false
            }));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = new Dictionary<string, object>
            {
                {"public", true},
                {"name", "Default"}
            },
            Size = 1000,
            ModifiedTime = DateTime.Now,
            Path = "."
        });

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager);

        // Act
        var result = await datasetsManager.GetEntry(orgSlug, dsSlug);

        // Assert
        result.ShouldNotBeNull();
        result.Count().ShouldBe(1);

        var entry = result.First();
        var permissionsDict = JsonConvert.DeserializeObject<Dictionary<string, bool>>(
            JsonConvert.SerializeObject(entry.Properties["permissions"]));

        permissionsDict["canRead"].ShouldBeTrue();
        permissionsDict["canWrite"].ShouldBeFalse();
        permissionsDict["canDelete"].ShouldBeFalse();
    }

    [Test]
    public async Task List_IncludesPermissionsInDatasets()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;

        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to return specific permissions
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(), AccessType.Read))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = true,
                CanWrite = true,
                CanDelete = false
            }));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = new Dictionary<string, object>
            {
                {"public", true},
                {"name", "Default"}
            },
            Size = 1000,
            ModifiedTime = DateTime.Now,
            Path = "."
        });

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager);

        // Act
        var result = await datasetsManager.List(orgSlug);

        // Assert
        result.ShouldNotBeNull();
        var datasets = result.ToList();
        datasets.Count().ShouldBe(1);

        var dataset = datasets.First();
        dataset.Permissions.ShouldNotBeNull();
        dataset.Permissions.CanRead.ShouldBeTrue();
        dataset.Permissions.CanWrite.ShouldBeTrue();
        dataset.Permissions.CanDelete.ShouldBeFalse();

        // Verify GetDatasetPermissions was called
        _authManagerMock.Verify(x => x.GetDatasetPermissions(It.IsAny<Dataset>()), Times.Once);
    }

    [Test]
    public async Task List_WithReadOnlyAccess_ReturnsCorrectPermissions()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;

        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to return read-only permissions
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(), AccessType.Read))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = true,
                CanWrite = false,
                CanDelete = false
            }));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = new Dictionary<string, object>
            {
                {"public", true},
                {"name", "Default"}
            },
            Size = 1000,
            ModifiedTime = DateTime.Now,
            Path = "."
        });

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager);

        // Act
        var result = await datasetsManager.List(orgSlug);

        // Assert
        result.ShouldNotBeNull();
        var datasets = result.ToList();
        datasets.Count().ShouldBe(1);

        var dataset = datasets.First();
        dataset.Permissions.ShouldNotBeNull();
        dataset.Permissions.CanRead.ShouldBeTrue();
        dataset.Permissions.CanWrite.ShouldBeFalse();
        dataset.Permissions.CanDelete.ShouldBeFalse();
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
    ""DdbPath"": ""./ddb""
}
  ");

    #endregion

    #region TestContexts

    private static RegistryContext GetTest1Context()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "RegistryDatabase-" + Guid.NewGuid())
            .Options;

        // Insert seed data into the database using one instance of the context
        using (var context = new RegistryContext(options))
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
        }

        return new RegistryContext(options);
    }

    #endregion
}
