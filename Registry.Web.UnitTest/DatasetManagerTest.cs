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
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
public class DatasetManagerTest : TestBase
{

    private Mock<IAuthManager> _authManagerMock;
    private MutableOptions<AppSettings> _settingsOptions;
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
        _settingsOptions = TestSettings.Mutable();
        _authManagerMock = new Mock<IAuthManager>();
        _datasetManagerMock = new Mock<IDatasetsManager>();
        _ddbFactoryMock = new Mock<IDdbManager>();
        _objectsManagerMock = new Mock<IObjectsManager>();
        _stacManagerMock = new Mock<IStacManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _backgroundJobMock = new Mock<IBackgroundJobsProcessor>();
        _cacheManager = TestCaches.InMemoryWithDatasetVisibility();
        _datasetsManagerLogger = CreateTestLogger<DatasetsManager>();
        _fileSystem = new FileSystem();
    }

    [Test]
    public async Task List_Default_Ok()
    {
        const string expectedSlug = MagicStrings.DefaultDatasetSlug;
        const string expectedName = "Default";


        await using var context = GetTest1Context();
        _settingsOptions.Value = _settings;
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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions), _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

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
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        var list = (await datasetsManager.List(MagicStrings.PublicOrganizationSlug)).ToArray();

        list.Count().ShouldBe(1);

        var pub = list.First();

        pub.Slug.ShouldBe(expectedSlug);

        // TODO: Check test data: this should be true
        pub.Properties["public"].ShouldBe(true);
        pub.Properties["name"].ShouldBe(expectedName);


    }

    [Test]
    public async Task AddNew_StoresTheCreationDateInUtc()
    {
        // The dates are sent as UTC (DateTimeZoneHandling.Utc): a local time stored here showed
        // new datasets as created in the future on hosts east of UTC
        await using var context = GetTest1Context();
        _settingsOptions.Value = _settings;
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));
        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.Meta).Returns(new Mock<Registry.Ports.DroneDB.IMetaManager>().Object);
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry { Properties = new Dictionary<string, object>() });
        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);
        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object),
            _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions,
            TestCaches.Summaries(_ddbFactoryMock.Object));

        var before = DateTime.UtcNow;
        await datasetsManager.AddNew(MagicStrings.PublicOrganizationSlug, new DatasetNewDto { Slug = "utc-check" });

        var stored = context.Datasets.Single(d => d.Slug == "utc-check").CreationDate;
        stored.ShouldBeInRange(before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Test]
    public async Task Get_WithPermissions_PopulatesPermissionsProperty()
    {
        // Arrange
        const string orgSlug = MagicStrings.PublicOrganizationSlug;
        const string dsSlug = MagicStrings.DefaultDatasetSlug;

        await using var context = GetTest1Context();
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([]);
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.FindSummaries(It.IsAny<EntryQuery>())).Returns([]);
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
        _settingsOptions.Value = _settings;

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

        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, _fileSystem, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
    public async Task MoveToOrganization_SourceSlugEmpty_ThrowsBadRequestException()
    {
        // Arrange
        await using var context = GetMoveTestContext();
        var (utils, fileSystemMock) = SetupMoveTestDependencies(context);
        SetupAdminUser();

        var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
            _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object,
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act & Assert
        Task<IEnumerable<MoveDatasetResultDto>> Action() => datasetsManager.MoveToOrganization("", ["dataset-1"], "dest-org");
        await Should.ThrowAsync<BadRequestException>((Func<Task<IEnumerable<MoveDatasetResultDto>>>)Action);
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization(null, ["dataset-1"], "dest-org");
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", ["dataset-1"], "");
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", [], "dest-org");
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", ["dataset-1"], "source-org");
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", ["dataset-1"], "dest-org")).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", ["dataset-1", "dataset-2"], "dest-org")).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", ["non-existent-dataset"], "dest-org")).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act - Mix of existing and non-existing datasets
        var results = (await datasetsManager.MoveToOrganization("source-org",
            ["dataset-1", "non-existent", "dataset-2"], "dest-org")).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            ["conflicting-dataset"], "dest-org", ConflictResolutionStrategy.HaltOnConflict)).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            ["conflicting-dataset"], "dest-org", ConflictResolutionStrategy.Rename)).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            ["conflicting-dataset"], "dest-org", ConflictResolutionStrategy.Rename)).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org",
            ["conflicting-dataset"], "dest-org", ConflictResolutionStrategy.Overwrite)).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        var results = (await datasetsManager.MoveToOrganization("source-org", ["dataset-1"], "dest-org")).ToArray();

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("non-existent-org", ["dataset-1"], "dest-org");
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act & Assert
        var action = () => datasetsManager.MoveToOrganization("source-org", ["dataset-1"], "non-existent-org");
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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act
        await datasetsManager.MoveToOrganization("source-org", ["dataset-1", "dataset-2"], "dest-org");

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
            _authManagerMock.Object, new DatasetAccessResolver(utils, _authManagerMock.Object, _ddbFactoryMock.Object), _cacheManager, fileSystemMock.Object, _backgroundJobMock.Object, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));

        // Act - Move datasets, some will conflict, some won't
        var results = (await datasetsManager.MoveToOrganization("source-org",
            ["unique-dataset", "conflicting-dataset"], "dest-org", ConflictResolutionStrategy.Rename)).ToArray();

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
        _settingsOptions.Value = _settings;
        var fileSystemMock = new Mock<IFileSystem>();
        var utils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));
        return (utils, fileSystemMock);
    }

    /// <summary>A private source and destination organization (owner "admin-id") with the given datasets.</summary>
    private static RegistryContext GetMoveTestContext(string[] sourceDatasets, string[] destDatasets)
    {
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            var sourceOrg = Seed.Organization(context, "source-org", "Source Organization", ownerId: "admin-id",
                description: "Source organization for testing");
            foreach (var slug in sourceDatasets)
                Seed.Dataset(context, sourceOrg, slug);

            var destOrg = Seed.Organization(context, "dest-org", "Destination Organization", ownerId: "admin-id",
                description: "Destination organization for testing");
            foreach (var slug in destDatasets)
                Seed.Dataset(context, destOrg, slug);

            context.SaveChanges();
        });
    }

    private static RegistryContext GetMoveTestContext() =>
        GetMoveTestContext(["dataset-1", "dataset-2"], Array.Empty<string>());

    private static RegistryContext GetMoveTestContextWithConflict() =>
        GetMoveTestContext(["conflicting-dataset"], ["conflicting-dataset"]);

    private static RegistryContext GetMoveTestContextWithMultipleConflicts() =>
        GetMoveTestContext(["conflicting-dataset"], ["conflicting-dataset", "conflicting-dataset_1", "conflicting-dataset_2"]);

    private static RegistryContext GetMoveTestContextWithMixedConflicts() =>
        GetMoveTestContext(["unique-dataset", "conflicting-dataset"], ["conflicting-dataset"]);

    #endregion

    #endregion


    #region Test Data

    private readonly AppSettings _settings = TestSettings.Create(s => s.DatasetsPath = "./test-datasets");

    #endregion

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

    private static RegistryContext GetContextWithMultipleDatasets()
    {
        return TestDb.CreateContext<RegistryContext>(context =>
        {
            var organization = Seed.PublicOrganization(context);

            // One public dataset and one private dataset
            Seed.Dataset(context, organization, "public-dataset");
            Seed.Dataset(context, organization, "private-dataset");
            context.SaveChanges();
        });
    }

    #endregion
}
