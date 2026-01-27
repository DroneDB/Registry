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
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
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
    private Mock<IBackgroundJobsProcessor> _backgroundJobMock;
    private ICacheManager _cacheManager;
    private IFileSystem _fileSystem;

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
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _cacheManager = CreateTestCacheManager();
        RegisterDatasetVisibilityCacheProvider(_cacheManager);
        _datasetsManagerLogger = CreateTestLogger<DatasetsManager>();
        _fileSystem = new FileSystem();
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
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = true,
                CanWrite = true,
                CanDelete = true
            }));

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
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

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
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

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
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

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
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

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
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

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
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var result = await datasetsManager.List(orgSlug);

        // Assert
        result.ShouldNotBeNull();
        var datasets = result.ToList();
        datasets.Count.ShouldBe(1);

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
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var result = await datasetsManager.List(orgSlug);

        // Assert
        result.ShouldNotBeNull();
        var datasets = result.ToList();
        datasets.Count.ShouldBe(1);

        var dataset = datasets.First();
        dataset.Permissions.ShouldNotBeNull();
        dataset.Permissions.CanRead.ShouldBeTrue();
        dataset.Permissions.CanWrite.ShouldBeFalse();
        dataset.Permissions.CanDelete.ShouldBeFalse();
    }

    [Test]
    public async Task List_ExcludesPrivateDatasetsUserCannotRead()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;

        await using var context = GetContextWithMultipleDatasets();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to allow org access
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(), AccessType.Read))
            .Returns(Task.FromResult(true));

        // Setup permissions - first dataset readable, second not
        var callCount = 0;
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call: can read, second call: cannot read
                return callCount == 1
                    ? new DatasetPermissionsDto { CanRead = true, CanWrite = false, CanDelete = false }
                    : new DatasetPermissionsDto { CanRead = false, CanWrite = false, CanDelete = false };
            });

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = new Dictionary<string, object>
            {
                {"public", true},
                {"name", "Test"}
            },
            Size = 1000,
            ModifiedTime = DateTime.Now,
            Path = "."
        });

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()))
            .Returns(ddbMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var result = await datasetsManager.List(orgSlug);

        // Assert
        result.ShouldNotBeNull();
        var datasets = result.ToList();
        // Should only include the dataset the user can read
        datasets.Count.ShouldBe(1);
        datasets.First().Slug.ShouldBe("public-dataset");
    }

    [Test]
    public async Task List_ReturnsEmptyWhenNoReadableDatasets()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;

        await using var context = GetTest1Context();
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        // Setup auth manager to allow org access but deny dataset read
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(), AccessType.Read))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetDatasetPermissions(It.IsAny<Dataset>()))
            .Returns(Task.FromResult(new DatasetPermissionsDto
            {
                CanRead = false,
                CanWrite = false,
                CanDelete = false
            }));

        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, _fileSystem, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var result = await datasetsManager.List(orgSlug);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldBeEmpty();

        // DDB should never be called for non-readable datasets
        _ddbFactoryMock.Verify(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    #region MoveToOrganization Tests

    [Test]
    public async Task MoveToOrganization_NonAdminUser_ThrowsUnauthorizedException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupStandardUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1" }, "dest-org");
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task MoveToOrganization_SourceSlugEmpty_ThrowsBadRequestException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("", new[] { "dataset-1" }, "dest-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task MoveToOrganization_SourceSlugNull_ThrowsBadRequestException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization(null, new[] { "dataset-1" }, "dest-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task MoveToOrganization_DestSlugEmpty_ThrowsBadRequestException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1" }, "");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task MoveToOrganization_DatasetSlugsNull_ThrowsBadRequestException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", null, "dest-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task MoveToOrganization_DatasetSlugsEmpty_ThrowsBadRequestException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", Array.Empty<string>(), "dest-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task MoveToOrganization_SourceEqualsDestination_ThrowsBadRequestException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1" }, "source-org");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task MoveToOrganization_SingleDatasetNoConflict_MovesSuccessfully()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1" }, "dest-org")).ToArray();

        // Assert
        results.Length.ShouldBe(1);
        results[0].Success.ShouldBeTrue();
        results[0].OriginalSlug.ShouldBe("dataset-1");
        results[0].NewSlug.ShouldBe("dataset-1");

        // Verify dataset was moved in database
        var movedDataset = await context.Datasets
            .Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Slug == "dataset-1");
        movedDataset.ShouldNotBeNull();
        movedDataset.Organization.Slug.ShouldBe("dest-org");

        // Verify file system operations
        fileSystemMock.Verify(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
        fileSystemMock.Verify(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task MoveToOrganization_MultipleDatasets_MovesAllSuccessfully()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1", "dataset-2" }, "dest-org")).ToArray();

        // Assert
        results.Length.ShouldBe(2);
        results.ShouldAllBe(r => r.Success);

        // Verify both datasets were moved in database
        var movedDatasets = await context.Datasets
            .Include(d => d.Organization)
            .Where(d => d.Slug == "dataset-1" || d.Slug == "dataset-2")
            .ToListAsync();

        movedDatasets.ShouldAllBe(d => d.Organization.Slug == "dest-org");
    }

    [Test]
    public async Task MoveToOrganization_DatasetNotFound_ReturnsErrorInResult()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", new[] { "non-existent-dataset" }, "dest-org")).ToArray();

        // Assert
        results.Length.ShouldBe(1);
        results[0].Success.ShouldBeFalse();
        results[0].OriginalSlug.ShouldBe("non-existent-dataset");
        results[0].Error.ShouldContain("not found");
    }

    [Test]
    public async Task MoveToOrganization_PartialSuccess_ReturnsCorrectResults()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act - Mix of existing and non-existing datasets
        var results = (await datasetsManager.MoveToOrganization("source-org",
            new[] { "dataset-1", "non-existent", "dataset-2" }, "dest-org")).ToArray();

        // Assert
        results.Length.ShouldBe(3);
        results.Count(r => r.Success).ShouldBe(2);
        results.Count(r => !r.Success).ShouldBe(1);
        results.First(r => !r.Success).OriginalSlug.ShouldBe("non-existent");
    }

    [Test]
    public async Task MoveToOrganization_ConflictWithHaltOnConflict_ReturnsError()
    {
        // Arrange
        await using var context = GetMoveTestContextWithConflict();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            new[] { "conflicting-dataset" }, "dest-org", ConflictResolutionStrategy.HaltOnConflict)).ToArray();

        // Assert
        results.Length.ShouldBe(1);
        results[0].Success.ShouldBeFalse();
        results[0].Error.ShouldContain("already exists");
    }

    [Test]
    public async Task MoveToOrganization_ConflictWithRename_RenamesDataset()
    {
        // Arrange
        await using var context = GetMoveTestContextWithConflict();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            new[] { "conflicting-dataset" }, "dest-org", ConflictResolutionStrategy.Rename)).ToArray();

        // Assert
        results.Length.ShouldBe(1);
        results[0].Success.ShouldBeTrue();
        results[0].OriginalSlug.ShouldBe("conflicting-dataset");
        results[0].NewSlug.ShouldBe("conflicting-dataset_1");

        // Verify dataset was renamed in database
        var renamedDataset = await context.Datasets
            .Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Slug == "conflicting-dataset_1");
        renamedDataset.ShouldNotBeNull();
        renamedDataset.Organization.Slug.ShouldBe("dest-org");
    }

    [Test]
    public async Task MoveToOrganization_ConflictWithRename_IncrementsSuffixUntilUnique()
    {
        // Arrange
        await using var context = GetMoveTestContextWithMultipleConflicts();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            new[] { "conflicting-dataset" }, "dest-org", ConflictResolutionStrategy.Rename)).ToArray();

        // Assert
        results.Length.ShouldBe(1);
        results[0].Success.ShouldBeTrue();
        // Should be _3 since _1 and _2 already exist
        results[0].NewSlug.ShouldBe("conflicting-dataset_3");
    }

    [Test]
    public async Task MoveToOrganization_ConflictWithOverwrite_DeletesExistingAndMoves()
    {
        // Arrange
        await using var context = GetMoveTestContextWithConflict();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);
        _objectsManagerMock.Setup(x => x.DeleteAll(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            new[] { "conflicting-dataset" }, "dest-org", ConflictResolutionStrategy.Overwrite)).ToArray();

        // Assert
        results.Length.ShouldBe(1);
        results[0].Success.ShouldBeTrue();
        results[0].OriginalSlug.ShouldBe("conflicting-dataset");
        results[0].NewSlug.ShouldBe("conflicting-dataset");

        // Verify the dataset in destination is now the one from source
        var datasets = await context.Datasets
            .Include(d => d.Organization)
            .Where(d => d.Slug == "conflicting-dataset")
            .ToListAsync();

        datasets.Count.ShouldBe(1);
        datasets[0].Organization.Slug.ShouldBe("dest-org");
    }

    [Test]
    public async Task MoveToOrganization_FilesNotExist_StillMovesDatabase()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(false);
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1" }, "dest-org")).ToArray();

        // Assert
        results.Length.ShouldBe(1);
        results[0].Success.ShouldBeTrue();

        // Verify file operations were not called
        fileSystemMock.Verify(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        fileSystemMock.Verify(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task MoveToOrganization_SourceOrganizationNotFound_ThrowsNotFoundException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("non-existent-org", new[] { "dataset-1" }, "dest-org");
        await Should.ThrowAsync<NotFoundException>(action);
    }

    [Test]
    public async Task MoveToOrganization_DestinationOrganizationNotFound_ThrowsNotFoundException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1" }, "non-existent-org");
        await Should.ThrowAsync<NotFoundException>(action);
    }

    [Test]
    public async Task MoveToOrganization_ClearsStacCache_AfterSuccessfulMove()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act
        await datasetsManager.MoveToOrganization("source-org", new[] { "dataset-1", "dataset-2" }, "dest-org");

        // Assert - Verify ClearCache was called for each moved dataset
        _stacManagerMock.Verify(x => x.ClearCache(It.IsAny<Dataset>()), Times.Exactly(2));
    }

    [Test]
    public async Task MoveToOrganization_BatchConflictHandling_HandlesMultipleConflictsCorrectly()
    {
        // Arrange
        await using var context = GetMoveTestContextWithMixedConflicts();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        fileSystemMock.Setup(x => x.FolderExists(It.IsAny<string>())).Returns(true);
        fileSystemMock.Setup(x => x.FolderCopy(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()));
        fileSystemMock.Setup(x => x.FolderDelete(It.IsAny<string>(), It.IsAny<bool>()));
        _stacManagerMock.Setup(x => x.ClearCache(It.IsAny<Dataset>())).Returns(Task.CompletedTask);

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _appSettingsMock.Object);

        // Act - Move datasets, some will conflict, some won't
        var results = (await datasetsManager.MoveToOrganization("source-org",
            new[] { "unique-dataset", "conflicting-dataset" }, "dest-org", ConflictResolutionStrategy.Rename)).ToArray();

        // Assert
        results.Length.ShouldBe(2);

        var uniqueResult = results.First(r => r.OriginalSlug == "unique-dataset");
        uniqueResult.Success.ShouldBeTrue();
        uniqueResult.NewSlug.ShouldBe("unique-dataset"); // No rename needed

        var conflictResult = results.First(r => r.OriginalSlug == "conflicting-dataset");
        conflictResult.Success.ShouldBeTrue();
        conflictResult.NewSlug.ShouldBe("conflicting-dataset_1"); // Renamed due to conflict
    }

    #region MoveToOrganization Helper Methods

    private void SetupAdminUser()
    {
        var adminUser = new User
        {
            UserName = "admin",
            Email = "admin@example.com",
            Id = "admin-id"
        };

        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(adminUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
    }

    private void SetupStandardUser()
    {
        var standardUser = new User
        {
            UserName = "standard",
            Email = "standard@example.com",
            Id = "standard-id"
        };

        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(standardUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
    }

    private (IUtils utils, Mock<IFileSystem> fileSystemMock) SetupMoveTestDependencies(RegistryContext context)
    {
        _appSettingsMock.Setup(o => o.Value).Returns(_settings);
        var fileSystemMock = new Mock<IFileSystem>();
        var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);
        return (utils, fileSystemMock);
    }

    private static RegistryContext GetMoveTestContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .Options;

        using (var context = new RegistryContext(options))
        {
            var sourceOrg = new Organization
            {
                Slug = "source-org",
                Name = "Source Organization",
                CreationDate = DateTime.Now,
                Description = "Source organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>
                {
                    new Dataset { Slug = "dataset-1", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() },
                    new Dataset { Slug = "dataset-2", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() }
                }
            };

            var destOrg = new Organization
            {
                Slug = "dest-org",
                Name = "Destination Organization",
                CreationDate = DateTime.Now,
                Description = "Destination organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>()
            };

            context.Organizations.AddRange(sourceOrg, destOrg);
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }

    private static RegistryContext GetMoveTestContextWithConflict()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .Options;

        using (var context = new RegistryContext(options))
        {
            var sourceOrg = new Organization
            {
                Slug = "source-org",
                Name = "Source Organization",
                CreationDate = DateTime.Now,
                Description = "Source organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>
                {
                    new Dataset { Slug = "conflicting-dataset", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() }
                }
            };

            var destOrg = new Organization
            {
                Slug = "dest-org",
                Name = "Destination Organization",
                CreationDate = DateTime.Now,
                Description = "Destination organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>
                {
                    new Dataset { Slug = "conflicting-dataset", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() }
                }
            };

            context.Organizations.AddRange(sourceOrg, destOrg);
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }

    private static RegistryContext GetMoveTestContextWithMultipleConflicts()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .Options;

        using (var context = new RegistryContext(options))
        {
            var sourceOrg = new Organization
            {
                Slug = "source-org",
                Name = "Source Organization",
                CreationDate = DateTime.Now,
                Description = "Source organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>
                {
                    new Dataset { Slug = "conflicting-dataset", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() }
                }
            };

            var destOrg = new Organization
            {
                Slug = "dest-org",
                Name = "Destination Organization",
                CreationDate = DateTime.Now,
                Description = "Destination organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>
                {
                    new Dataset { Slug = "conflicting-dataset", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() },
                    new Dataset { Slug = "conflicting-dataset_1", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() },
                    new Dataset { Slug = "conflicting-dataset_2", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() }
                }
            };

            context.Organizations.AddRange(sourceOrg, destOrg);
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }

    private static RegistryContext GetMoveTestContextWithMixedConflicts()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .Options;

        using (var context = new RegistryContext(options))
        {
            var sourceOrg = new Organization
            {
                Slug = "source-org",
                Name = "Source Organization",
                CreationDate = DateTime.Now,
                Description = "Source organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>
                {
                    new Dataset { Slug = "unique-dataset", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() },
                    new Dataset { Slug = "conflicting-dataset", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() }
                }
            };

            var destOrg = new Organization
            {
                Slug = "dest-org",
                Name = "Destination Organization",
                CreationDate = DateTime.Now,
                Description = "Destination organization for testing",
                IsPublic = false,
                OwnerId = "admin-id",
                Datasets = new List<Dataset>
                {
                    new Dataset { Slug = "conflicting-dataset", CreationDate = DateTime.Now, InternalRef = Guid.NewGuid() }
                }
            };

            context.Organizations.AddRange(sourceOrg, destOrg);
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }

    #endregion

    #endregion


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
    ""DatasetsPath"": ""./test-datasets"",
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

    private static RegistryContext GetContextWithMultipleDatasets()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: "RegistryDatabase-" + Guid.NewGuid())
            .Options;

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

            // One public dataset and one private dataset
            var publicDs = new Dataset
            {
                Slug = "public-dataset",
                CreationDate = DateTime.Now,
                InternalRef = Guid.NewGuid()
            };

            var privateDs = new Dataset
            {
                Slug = "private-dataset",
                CreationDate = DateTime.Now,
                InternalRef = Guid.NewGuid()
            };

            entity.Datasets = new List<Dataset> { publicDs, privateDs };

            context.Organizations.Add(entity);
            context.SaveChanges();
        }

        return new RegistryContext(options);
    }

    #endregion
}
