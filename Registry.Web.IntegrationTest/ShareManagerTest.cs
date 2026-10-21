using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Adapters.DroneDB;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Adapters;
using Registry.Test.Common;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services;
using Entry = Registry.Ports.DroneDB.Entry;
using IMetaManager = Registry.Ports.DroneDB.IMetaManager;
using Registry.Web.Test.Support;

namespace Registry.Web.Test;

[TestFixture]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
class ShareManagerTest : TestBase
{
    private ILogger<ShareManager> _shareManagerLogger;
    private ILogger<ObjectsManager> _objectManagerLogger;
    private ILogger<DdbManager> _ddbFactoryLogger;
    private ILogger<DatasetsManager> _datasetsManagerLogger;
    private ILogger<OrganizationsManager> _organizationsManagerLogger;
    private ILogger<BatchTokenGenerator> _batchTokenGeneratorLogger;
    private ILogger<NameGenerator> _nameGeneratorLogger;

    private MutableOptions<AppSettings> _settingsOptions;
    private Mock<IDdbManager> _ddbFactoryMock;
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IUtils> _utilsMock;
    private Mock<IObjectsManager> _objectsManagerMock;
    private Mock<IOrganizationsManager> _organizationsManagerMock;
    private Mock<IDatasetsManager> _datasetsManagerMock;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private ICacheManager _cacheManager;
    private Mock<IStacManager> _stacManagerMock;

    private IBackgroundJobsProcessor _backgroundJobsProcessor;
    private BuildPendingService _buildPendingService;

    private readonly FileSystem _fileSystem = new FileSystem();

    private static readonly IDdbWrapper DdbWrapper = new NativeDdbWrapper();

    private const string BaseTestFolder = "ShareManagerTest";

    private static readonly string Test4ArchiveUrl = TestData.Url("registry/Test4-new.zip");

    public ShareManagerTest()
    {
        //
    }

    [SetUp]
    public void Setup()
    {
        _settingsOptions = TestSettings.Mutable();
        _ddbFactoryMock = new Mock<IDdbManager>();
        _authManagerMock = new Mock<IAuthManager>();
        _utilsMock = new Mock<IUtils>();

        _objectsManagerMock = new Mock<IObjectsManager>();
        _organizationsManagerMock = new Mock<IOrganizationsManager>();
        _datasetsManagerMock = new Mock<IDatasetsManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _backgroundJobsProcessor = new SimpleBackgroundJobsProcessor();
        _cacheManager = TestCaches.InMemoryWithDatasetVisibility();
        _stacManagerMock = new Mock<IStacManager>();

        var bpsContext = TestDb.CreateContext<RegistryContext>();
        _buildPendingService = new BuildPendingService(
            bpsContext, _ddbFactoryMock.Object, _backgroundJobsProcessor, _cacheManager,
            Mock.Of<ILogger<BuildPendingService>>());

        _shareManagerLogger = CreateTestLogger<ShareManager>();
        _objectManagerLogger = CreateTestLogger<ObjectsManager>();
        _ddbFactoryLogger = CreateTestLogger<DdbManager>();
        _organizationsManagerLogger = CreateTestLogger<OrganizationsManager>();
        _datasetsManagerLogger = CreateTestLogger<DatasetsManager>();
        _batchTokenGeneratorLogger = CreateTestLogger<BatchTokenGenerator>();
        _nameGeneratorLogger = CreateTestLogger<NameGenerator>();

    }

    /*
    [Test]
    public async Task Initialize_NullParameters_BadRequest()
    {
        const string userName = "admin";

        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            UserName = userName,
            Email = "admin@example.com"
        }));
        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        _settingsOptions.Value = settings;

        var manager = new ShareManager(_settingsOptions, _shareManagerLogger, _objectsManagerMock.Object,
            _datasetsManagerMock.Object,
            _organizationsManagerMock.Object, _utilsMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(_utilsMock.Object, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy),
            new BatchTokenGenerator(_settingsOptions, _batchTokenGeneratorLogger),
            new NameGenerator(_settingsOptions, _nameGeneratorLogger), GetTest1Context(), TestScratch.Provider);

        await Should.ThrowAsync<BadRequestException>(async () => await manager.Initialize(null));

        // Now empty tag is supported
        //manager.Invoking(x => x.Initialize(new ShareInitDto())).Should().Throw<BadRequestException>();
        await Should.ThrowAsync<BadRequestException>(async () => await manager.Initialize(new ShareInitDto { Tag = "ciao" }));
    }*/

    [Test]
    public async Task Initialize_WithOrgWithoutDataset_GeneratedTag()
    {
        // INITIALIZATION & SETUP
        const string userName = "admin";

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

        await using var context = GetTest1Context();
        await using var appContext = GetAppTest1Context();

        var settings = ShareSettings();
        settings.DatasetsPath = test.TestFolder;
        _settingsOptions.Value = settings;

        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Organization>())).Returns(Task.FromResult(true));
        var user = new User
        {
            UserName = userName,
            Email = "admin@example.com",
            Id = Guid.NewGuid().ToString()
        };

        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(user));
        _authManagerMock.Setup(o => o.UserExists(user.Id)).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

        var attributes = new Dictionary<string, object>
        {
            { "public", true }
        };

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = attributes
        });

        var mockMeta = new MockMeta();
        ddbMock.Setup(x => x.Meta).Returns(mockMeta);

        var ddbMock2 = new Mock<IDDB>();
        // ddbMock.Setup(x => x.GetAttributesAsync(default))
        //     .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var ddbFactory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);
        var webUtils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var objectManager = ObjectsManagerFor(context, webUtils, ddbFactory);

        var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
            _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), _cacheManager, _fileSystem, _backgroundJobsProcessor, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));
        var organizationsManager = new OrganizationsManager(_authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), context, webUtils,
            datasetManager, appContext, TestSettings.Options<AccountOptions>(_settingsOptions), _organizationsManagerLogger,
            new OrganizationMembershipService(context, appContext, new UpperInvariantLookupNormalizer(),
                NullLogger<OrganizationMembershipService>.Instance));

        var shareManager = new ShareManager(_settingsOptions, _shareManagerLogger, objectManager,
            datasetManager, organizationsManager, webUtils, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy),
            new BatchTokenGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _batchTokenGeneratorLogger),
            new NameGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _nameGeneratorLogger), context, _fileSystem, TestScratch.Provider);

        // TEST

        // ListBatches
        var batches =
            (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
            .ToArray();
        batches.ShouldBeEmpty();

        // Initialize
        var initRes = await shareManager.Initialize(new ShareInitDto
        {
            Tag = MagicStrings.PublicOrganizationSlug + "/test"
        });

        initRes.ShouldNotBeNull();
        initRes.Token.ShouldNotBeNullOrWhiteSpace();

        // Commit
        var commitRes = await shareManager.Commit(initRes.Token);

        commitRes.Tag.OrganizationSlug.ShouldBe(MagicStrings.PublicOrganizationSlug);

        // ListBatches
        batches = (await shareManager.ListBatches(commitRes.Tag.OrganizationSlug, commitRes.Tag.DatasetSlug))
            .ToArray();

        batches.Length.ShouldBe(1);

        var batch = batches.First();

        batch.Token.ShouldBe(initRes.Token);
        batch.UserName.ShouldBe(userName);
        batch.End.ShouldNotBeNull();
        batch.Status.ShouldBe(BatchStatus.Committed);
        batch.Entries.Count().ShouldBe(0);
    }

    [Test]
    public async Task Initialize_WithoutTag_GeneratedTag()
    {
        // INITIALIZATION & SETUP
        const string userName = "admin";

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

        await using var context = GetTest1Context();
        await using var appContext = GetAppTest1Context();

        var settings = ShareSettings();
        settings.DatasetsPath = test.TestFolder;
        _settingsOptions.Value = settings;

        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Organization>())).Returns(Task.FromResult(true));
        var user = new User
        {
            UserName = userName,
            Email = "admin@example.com",
            Id = Guid.NewGuid().ToString()
        };
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(user));
        _authManagerMock.Setup(o => o.UserExists(user.Id)).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var attributes = new Dictionary<string, object>
        {
            { "public", true }
        };

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = attributes
        });

        var mockMeta = new MockMeta();
        ddbMock.Setup(x => x.Meta).Returns(mockMeta);

        var ddbMock2 = new Mock<IDDB>();
        // ddbMock.Setup(x => x.GetAttributesAsync(default))
        //     .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var ddbFactory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);
        var webUtils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var objectManager = ObjectsManagerFor(context, webUtils, ddbFactory);

        var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
            _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), _cacheManager, _fileSystem, _backgroundJobsProcessor, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));
        var organizationsManager = new OrganizationsManager(_authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), context, webUtils,
            datasetManager, appContext, TestSettings.Options<AccountOptions>(_settingsOptions), _organizationsManagerLogger,
            new OrganizationMembershipService(context, appContext, new UpperInvariantLookupNormalizer(),
                NullLogger<OrganizationMembershipService>.Instance));

        var shareManager = new ShareManager(_settingsOptions, _shareManagerLogger, objectManager,
            datasetManager, organizationsManager, webUtils, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy),
            new BatchTokenGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _batchTokenGeneratorLogger),
            new NameGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _nameGeneratorLogger), context, _fileSystem, TestScratch.Provider);

        // TEST

        // ListBatches
        var batches =
            (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
            .ToArray();
        batches.ShouldBeEmpty();

        // Initialize
        var initRes = await shareManager.Initialize(new ShareInitDto());

        initRes.ShouldNotBeNull();
        initRes.Token.ShouldNotBeNullOrWhiteSpace();
        //initRes.Tag.DatasetSlug.ShouldBe(datasetTestSlug);
        //initRes.Tag.OrganizationSlug.ShouldBe(userName);

        // ListBatches
        //batches = (await shareManager.ListBatches(initRes.Tag.OrganizationSlug, initRes.Tag.DatasetSlug)).ToArray();

        //batches.Count().ShouldBe(1);

        //var batch = batches.First();

        //batch.UserName.ShouldBe(userName);
        //batch.Status.ShouldBe(BatchStatus.Running);
        //batch.End.ShouldBeNull();

        // Commit
        var commitRes = await shareManager.Commit(initRes.Token);

        commitRes.Url.ShouldBe($"/r/{commitRes.Tag.OrganizationSlug}/{commitRes.Tag.DatasetSlug}");

        // ListBatches
        batches = (await shareManager.ListBatches(commitRes.Tag.OrganizationSlug, commitRes.Tag.DatasetSlug))
            .ToArray();

        batches.Length.ShouldBe(1);

        var batch = batches.First();

        batch.Token.ShouldBe(initRes.Token);
        batch.UserName.ShouldBe(userName);
        batch.End.ShouldNotBeNull();
        batch.Status.ShouldBe(BatchStatus.Committed);
        batch.Entries.Count().ShouldBe(0);
    }

    [Test]
    public async Task EndToEnd_HappyPathRollback()
    {
        // INITIALIZATION & SETUP
        const string userName = "admin";

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

        await using var context = GetTest1Context();
        await using var appContext = GetAppTest1Context();

        var settings = ShareSettings();
        settings.DatasetsPath = test.TestFolder;
        _settingsOptions.Value = settings;

        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Organization>())).Returns(Task.FromResult(true));

        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            UserName = userName,
            Email = "admin@example.com"
        }));
        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

        var attributes = new Dictionary<string, object>
        {
            { "public", true }
        };

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = attributes
        });

        var mockMeta = new MockMeta();
        ddbMock.Setup(x => x.Meta).Returns(mockMeta);

        var ddbMock2 = new Mock<IDDB>();
        // ddbMock.Setup(x => x.GetAttributesAsync(default))
        //     .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var ddbFactory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);
        var webUtils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var objectManager = ObjectsManagerFor(context, webUtils, ddbFactory);

        var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
            _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), _cacheManager, _fileSystem, _backgroundJobsProcessor, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));
        var organizationsManager = new OrganizationsManager(_authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), context, webUtils,
            datasetManager, appContext, TestSettings.Options<AccountOptions>(_settingsOptions), _organizationsManagerLogger,
            new OrganizationMembershipService(context, appContext, new UpperInvariantLookupNormalizer(),
                NullLogger<OrganizationMembershipService>.Instance));

        var shareManager = new ShareManager(_settingsOptions, _shareManagerLogger, objectManager,
            datasetManager, organizationsManager, webUtils, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy),
            new BatchTokenGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _batchTokenGeneratorLogger),
            new NameGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _nameGeneratorLogger), context, _fileSystem, TestScratch.Provider);

        // TEST

        const string fileName = "DJI_0028.JPG";
        const int fileSize = 3140384;
        const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
        const string organizationTestName = "test";
        const string datasetTestName = "First";
        const string organizationTestSlug = "test";
        const string datasetTestSlug = "first";
        var newFileUrl = TestData.Url("test-datasets/drone_dataset_brighton_beach/" + fileName);

        // ListBatches
        var batches =
            (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
            .ToArray();
        batches.ShouldBeEmpty();

        var res = await organizationsManager.AddNew(new OrganizationDto
        {
            Name = organizationTestName,
            IsPublic = true,
            Slug = organizationTestSlug
        });

        res.Description.ShouldBeNull();
        res.IsPublic.ShouldBeTrue();
        res.Slug.ShouldBe(organizationTestSlug);
        res.Name.ShouldBe(organizationTestName);

        // Initialize

        var initRes = await shareManager.Initialize(new ShareInitDto
        {
            Tag = organizationTestSlug + "/" + datasetTestSlug,
            DatasetName = datasetTestName
        });

        initRes.ShouldNotBeNull();
        initRes.Token.ShouldNotBeNullOrWhiteSpace();
        //initRes.Tag.DatasetSlug.ShouldBe(datasetTestSlug);
        //initRes.Tag.OrganizationSlug.ShouldBe(organizationTestSlug);

        // ListBatches
        batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

        batches.Length.ShouldBe(1);

        var batch = batches.First();

        batch.UserName.ShouldBe(userName);
        batch.Status.ShouldBe(BatchStatus.Running);
        batch.End.ShouldBeNull();

        // Upload
        var uploadRes =
            await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

        uploadRes.Path.ShouldBe(fileName);
        uploadRes.Size.ShouldBe(fileSize);
        uploadRes.Hash.ShouldBe(fileHash);

        // Rollback
        await shareManager.Rollback(initRes.Token);

        // Check cleanup
        var act = async () => await datasetManager.Get(organizationTestSlug, datasetTestSlug);
        await Should.ThrowAsync<NotFoundException>(act);
    }

    [Test]
    public async Task EndToEnd_HappyPath()
    {
        // INITIALIZATION & SETUP
        const string userName = "admin";

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

        await using var context = GetTest1Context();
        await using var appContext = GetAppTest1Context();

        var settings = ShareSettings();
        settings.DatasetsPath = test.TestFolder;
        _settingsOptions.Value = settings;

        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Organization>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            UserName = userName,
            Email = "admin@example.com"
        }));

        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

        var attributes = new Dictionary<string, object>
        {
            { "public", true }
        };

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = attributes
        });

        var metaMock = new Mock<IMetaManager>();
        metaMock.Setup(x => x.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()));

        var mockMeta = new MockMeta();
        ddbMock.Setup(x => x.Meta).Returns(mockMeta);

        var ddbMock2 = new Mock<IDDB>();
        // ddbMock.Setup(x => x.GetAttributesAsync(default))
        //     .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var ddbFactory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);
        var webUtils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var objectManager = ObjectsManagerFor(context, webUtils, ddbFactory);

        var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
            _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), _cacheManager, _fileSystem, _backgroundJobsProcessor, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));
        var organizationsManager = new OrganizationsManager(_authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), context, webUtils,
            datasetManager, appContext, TestSettings.Options<AccountOptions>(_settingsOptions), _organizationsManagerLogger,
            new OrganizationMembershipService(context, appContext, new UpperInvariantLookupNormalizer(),
                NullLogger<OrganizationMembershipService>.Instance));

        var shareManager = new ShareManager(_settingsOptions, _shareManagerLogger, objectManager,
            datasetManager, organizationsManager, webUtils, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy),
            new BatchTokenGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _batchTokenGeneratorLogger),
            new NameGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _nameGeneratorLogger), context, _fileSystem, TestScratch.Provider);

        // TEST

        const string fileName = "DJI_0028.JPG";
        const int fileSize = 3140384;
        const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
        const string organizationTestName = "test";
        const string datasetTestName = "First";
        const string organizationTestSlug = "test";
        const string datasetTestSlug = "first";
        var newFileUrl = TestData.Url("test-datasets/drone_dataset_brighton_beach/" + fileName);

        // ListBatches
        var batches =
            (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
            .ToArray();
        batches.ShouldBeEmpty();

        var res = await organizationsManager.AddNew(new OrganizationDto
        {
            Name = organizationTestName,
            IsPublic = true,
            Slug = organizationTestSlug
        });

        res.Description.ShouldBeNull();
        res.IsPublic.ShouldBeTrue();
        res.Slug.ShouldBe(organizationTestSlug);
        res.Name.ShouldBe(organizationTestName);

        // Initialize
        var initRes = await shareManager.Initialize(new ShareInitDto
        {
            Tag = organizationTestSlug + "/" + datasetTestSlug,
            DatasetName = datasetTestName
        });

        initRes.ShouldNotBeNull();
        initRes.Token.ShouldNotBeNullOrWhiteSpace();
        //initRes.Tag.DatasetSlug.ShouldBe(datasetTestSlug);
        //initRes.Tag.OrganizationSlug.ShouldBe(organizationTestSlug);

        // ListBatches
        batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

        batches.Length.ShouldBe(1);

        var batch = batches.First();

        batch.UserName.ShouldBe(userName);
        batch.Status.ShouldBe(BatchStatus.Running);
        batch.End.ShouldBeNull();

        // Upload
        var uploadRes =
            await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

        uploadRes.Path.ShouldBe(fileName);
        uploadRes.Size.ShouldBe(fileSize);
        uploadRes.Hash.ShouldBe(fileHash);

        // Commit
        var commitRes = await shareManager.Commit(initRes.Token);

        commitRes.Url.ShouldBe($"/r/{organizationTestSlug}/{datasetTestSlug}");
        commitRes.Tag.OrganizationSlug.ShouldBe(organizationTestSlug);
        commitRes.Tag.DatasetSlug.ShouldBe(datasetTestSlug);

        // ListBatches
        batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

        batches.Length.ShouldBe(1);

        batch = batches.First();

        batch.Token.ShouldBe(initRes.Token);
        batch.UserName.ShouldBe(userName);
        batch.End.ShouldNotBeNull();
        batch.Status.ShouldBe(BatchStatus.Committed);
        batch.Entries.Count().ShouldBe(1);
    }

    [Test]
    public async Task UploadStreamed_EndToEnd_AddsEntryAndCommits()
    {
        // INITIALIZATION & SETUP (mirrors EndToEnd_HappyPath but exercises the streamed upload path)
        const string userName = "admin";

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

        await using var context = GetTest1Context();
        await using var appContext = GetAppTest1Context();

        var settings = ShareSettings();
        settings.DatasetsPath = test.TestFolder;
        _settingsOptions.Value = settings;

        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Organization>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            UserName = userName,
            Email = "admin@example.com"
        }));

        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

        var attributes = new Dictionary<string, object>
        {
            { "public", true }
        };

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = attributes
        });

        var mockMeta = new MockMeta();
        ddbMock.Setup(x => x.Meta).Returns(mockMeta);

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var ddbFactory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);
        var webUtils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var objectManager = ObjectsManagerFor(context, webUtils, ddbFactory);

        var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
            _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), _cacheManager, _fileSystem,
            _backgroundJobsProcessor, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));
        var organizationsManager = new OrganizationsManager(_authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), context, webUtils,
            datasetManager, appContext, TestSettings.Options<AccountOptions>(_settingsOptions), _organizationsManagerLogger,
            new OrganizationMembershipService(context, appContext, new UpperInvariantLookupNormalizer(),
                NullLogger<OrganizationMembershipService>.Instance));

        var shareManager = new ShareManager(_settingsOptions, _shareManagerLogger, objectManager,
            datasetManager, organizationsManager, webUtils, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy),
            new BatchTokenGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _batchTokenGeneratorLogger),
            new NameGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _nameGeneratorLogger), context, _fileSystem, TestScratch.Provider);

        // TEST
        const string fileName = "DJI_0028.JPG";
        const int fileSize = 3140384;
        const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
        const string organizationTestName = "test";
        const string datasetTestName = "First";
        const string organizationTestSlug = "test";
        const string datasetTestSlug = "first";
        var newFileUrl = TestData.Url("test-datasets/drone_dataset_brighton_beach/" + fileName);

        await organizationsManager.AddNew(new OrganizationDto
        {
            Name = organizationTestName,
            IsPublic = true,
            Slug = organizationTestSlug
        });

        // Initialize
        var initRes = await shareManager.Initialize(new ShareInitDto
        {
            Tag = organizationTestSlug + "/" + datasetTestSlug,
            DatasetName = datasetTestName
        });

        initRes.ShouldNotBeNull();
        initRes.Token.ShouldNotBeNullOrWhiteSpace();

        // GetUploadTarget resolves the running batch's org/dataset
        var (orgSlug, dsSlug) = await shareManager.GetUploadTarget(initRes.Token);
        orgSlug.ShouldBe(organizationTestSlug);
        dsSlug.ShouldBe(datasetTestSlug);

        // Stream to a temp file on the dataset volume, then finalize via the streamed path
        var data = CommonUtils.SmartDownloadData(newFileUrl);
        await using var ms = new MemoryStream(data);
        var streamed = await objectManager.StreamToTempAsync(orgSlug, dsSlug, ms);

        var uploadRes = await shareManager.UploadStreamed(initRes.Token, fileName, streamed);

        uploadRes.Path.ShouldBe(fileName);
        uploadRes.Size.ShouldBe(fileSize);
        uploadRes.Hash.ShouldBe(fileHash);

        // The streamed temp file must be consumed by the commit
        File.Exists(streamed.TempPath).ShouldBeFalse();

        // Commit
        var commitRes = await shareManager.Commit(initRes.Token);

        commitRes.Url.ShouldBe($"/r/{organizationTestSlug}/{datasetTestSlug}");
        commitRes.Tag.OrganizationSlug.ShouldBe(organizationTestSlug);
        commitRes.Tag.DatasetSlug.ShouldBe(datasetTestSlug);

        var batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();
        batches.Length.ShouldBe(1);

        var batch = batches.First();
        batch.Token.ShouldBe(initRes.Token);
        batch.Status.ShouldBe(BatchStatus.Committed);
        batch.Entries.Count().ShouldBe(1);
    }

    [Test]
    public async Task EndToEnd_ShareInit_After_ShareInit()
    {
        // INITIALIZATION & SETUP
        const string userName = "admin";

        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

        await using var context = GetTest1Context();
        await using var appContext = GetAppTest1Context();

        var settings = ShareSettings();
        settings.DatasetsPath = test.TestFolder;
        _settingsOptions.Value = settings;

        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Organization>())).Returns(Task.FromResult(true));

        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            UserName = userName,
            Email = "admin@example.com"
        }));
        _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

        var attributes = new Dictionary<string, object>
        {
            { "public", true }
        };

        var ddbMock = new Mock<IDDB>();
        ddbMock.Setup(x => x.GetInfo()).Returns(new Entry
        {
            Properties = attributes
        });

        var mockMeta = new MockMeta();
        ddbMock.Setup(x => x.Meta).Returns(mockMeta);

        var ddbMock2 = new Mock<IDDB>();
        // ddbMock.Setup(x => x.GetAttributesAsync(default))
        //     .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

        var ddbFactory = new DdbManager(TestSettings.Options<StorageOptions>(_settingsOptions), _ddbFactoryLogger, DdbWrapper);
        var webUtils = new WebUtils(_authManagerMock.Object, context, TestSettings.Options<AccountOptions>(_settingsOptions), TestSettings.Options<HttpHostOptions>(_settingsOptions),
            _httpContextAccessorMock.Object, TestCaches.Summaries(_ddbFactoryMock.Object));

        var objectManager = ObjectsManagerFor(context, webUtils, ddbFactory);

        var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
            _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), _cacheManager, _fileSystem, _backgroundJobsProcessor, _settingsOptions, TestCaches.Summaries(_ddbFactoryMock.Object));
        var organizationsManager = new OrganizationsManager(_authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy), context, webUtils,
            datasetManager, appContext, TestSettings.Options<AccountOptions>(_settingsOptions), _organizationsManagerLogger,
            new OrganizationMembershipService(context, appContext, new UpperInvariantLookupNormalizer(),
                NullLogger<OrganizationMembershipService>.Instance));

        var shareManager = new ShareManager(_settingsOptions, _shareManagerLogger, objectManager,
            datasetManager, organizationsManager, webUtils, _authManagerMock.Object,
            new DatasetAccessResolver(webUtils, _authManagerMock.Object, _ddbFactoryMock.Object, TestPaths.Policy),
            new BatchTokenGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _batchTokenGeneratorLogger),
            new NameGenerator(TestSettings.Options<ShareOptions>(_settingsOptions), _nameGeneratorLogger), context, _fileSystem, TestScratch.Provider);

        // TEST

        const string fileName = "DJI_0028.JPG";
        const int fileSize = 3140384;
        const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
        const string organizationTestName = "test";
        const string datasetTestName = "First";
        const string organizationTestSlug = "test";
        const string datasetTestSlug = "first";
        var newFileUrl = TestData.Url("test-datasets/drone_dataset_brighton_beach/" + fileName);

        // ListBatches
        var batches =
            (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
            .ToArray();
        batches.ShouldBeEmpty();

        await organizationsManager.AddNew(new OrganizationDto
        {
            Name = organizationTestName,
            IsPublic = true,
            Slug = organizationTestSlug
        });

        // Initialize
        var initRes = await shareManager.Initialize(new ShareInitDto
        {
            Tag = organizationTestSlug + "/" + datasetTestSlug,
            DatasetName = datasetTestName,
        });

        initRes.ShouldNotBeNull();
        initRes.Token.ShouldNotBeNullOrWhiteSpace();
        //initRes.Tag.DatasetSlug.ShouldBe(datasetTestSlug);
        //initRes.Tag.OrganizationSlug.ShouldBe(organizationTestSlug);

        // ListBatches
        batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

        batches.Length.ShouldBe(1);


        // Upload
        await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

        // Initialize
        var newInitRes = await shareManager.Initialize(new ShareInitDto
        {
            Tag = organizationTestSlug + "/" + datasetTestSlug,
            DatasetName = datasetTestName,
        });

        initRes.Token.ShouldNotBeNullOrWhiteSpace();

        // ListBatches
        batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

        batches.Length.ShouldBe(2);

        var oldBatch = batches.FirstOrDefault(item => item.Token == initRes.Token);
        oldBatch.ShouldNotBeNull();

        // ReSharper disable once PossibleNullReferenceException
        oldBatch.End.ShouldNotBeNull();
        oldBatch.Status.ShouldBe(BatchStatus.Rolledback);

        var newBatch = batches.FirstOrDefault(item => item.Token == newInitRes.Token);
        newBatch.ShouldNotBeNull();

        // ReSharper disable once PossibleNullReferenceException
        newBatch.End.ShouldBeNull();
        newBatch.Status.ShouldBe(BatchStatus.Running);

        // Upload to old batch -> Exception
        var act1 = async () => await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));
        await Should.ThrowAsync<BadRequestException>(act1);

        // Commit old batch -> Exception
        var act2 = async () => await shareManager.Commit(initRes.Token);
        await Should.ThrowAsync<BadRequestException>(act2);

        // Fix
        context.Set<Data.Models.Entry>().Local.Clear();

        // Upload to new batch
        var uploadRes =
            await shareManager.Upload(newInitRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));
        uploadRes.Path.ShouldBe(fileName);
        uploadRes.Size.ShouldBe(fileSize);
        uploadRes.Hash.ShouldBe(fileHash);

        // Commit
        var commitRes = await shareManager.Commit(newInitRes.Token);

        commitRes.Url.ShouldBe($"/r/{organizationTestSlug}/{datasetTestSlug}");
        commitRes.Tag.OrganizationSlug.ShouldBe(organizationTestSlug);
        commitRes.Tag.DatasetSlug.ShouldBe(datasetTestSlug);

        // ListBatches
        batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

        batches.Length.ShouldBe(2);

        //batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

        //batches.Count().ShouldBe(1);
        //var batch = batches.First();
        //batch.Token.ShouldBe(token);
        //batch.UserName.ShouldBe("admin");
        //batch.Entries.Count().ShouldBe(1);
        //batch.End.ShouldNotBeNull();
    }


    #region Test Data

    // The manager-test defaults plus the share/batch settings these tests exercise.
    private static AppSettings ShareSettings() => TestSettings.Create(s =>
    {
        s.StoragePath = "./Data/Ddb";
        s.MaxRequestBodySize = 52428800;
        s.BatchTokenLength = 32;
        s.RandomDatasetNameLength = 16;
        s.CachePath = "./cache";
    });

    #endregion

    #region SUT factories

    private ObjectsManager ObjectsManagerFor(RegistryContext context, IUtils utils, IDdbManager ddbManager) =>
        new ObjectsManagerBuilder(context)
            .WithLogger(_objectManagerLogger)
            .WithSettings(_settingsOptions)
            .WithDdbManager(ddbManager)
            .WithUtils(utils)
            .WithAuthManager(_authManagerMock.Object)
            .WithCacheManager(_cacheManager)
            .WithFileSystem(_fileSystem)
            .WithBackgroundJobs(_backgroundJobsProcessor)
            .WithDdbWrapper(DdbWrapper)
            .WithBuildPendingService(_buildPendingService)
            .Build();

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
