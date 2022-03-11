using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
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
using Registry.Ports.DroneDB.Models;
using Registry.Web.Services;
using Attributes = Registry.Ports.DroneDB.Models.EntryAttributes;
using Entry = Registry.Ports.DroneDB.Models.Entry;
using IMetaManager = Registry.Ports.DroneDB.IMetaManager;

namespace Registry.Web.Test
{
    class ShareManagerTest
    {
        private Logger<ShareManager> _shareManagerLogger;
        private Logger<ObjectsManager> _objectManagerLogger;
        private Logger<DdbManager> _ddbFactoryLogger;
        private Logger<DatasetsManager> _datasetsManagerLogger;
        private Logger<OrganizationsManager> _organizationsManagerLogger;
        private Logger<BatchTokenGenerator> _batchTokenGeneratorLogger;
        private Logger<NameGenerator> _nameGeneratorLogger;

        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IDdbManager> _ddbFactoryMock;
        private Mock<IAuthManager> _authManagerMock;
        private Mock<IUtils> _utilsMock;
        private Mock<IObjectsManager> _objectsManagerMock;
        private Mock<IOrganizationsManager> _organizationsManagerMock;
        private Mock<IDatasetsManager> _datasetsManagerMock;
        private Mock<IHttpContextAccessor> _httpContextAccessorMock;
        private Mock<ICacheManager> _cacheManagerMock;
        private IBackgroundJobsProcessor _backgroundJobsProcessor;

        private readonly FileSystem _fileSystem = new FileSystem();

        private const string BaseTestFolder = "ShareManagerTest";

        private const string Test4ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/Test4-new.zip";

        public ShareManagerTest()
        {
            //
        }

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryMock = new Mock<IDdbManager>();
            _authManagerMock = new Mock<IAuthManager>();
            _utilsMock = new Mock<IUtils>();

            _objectsManagerMock = new Mock<IObjectsManager>();
            _organizationsManagerMock = new Mock<IOrganizationsManager>();
            _datasetsManagerMock = new Mock<IDatasetsManager>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            _backgroundJobsProcessor = new SimpleBackgroundJobsProcessor();
            _cacheManagerMock = new Mock<ICacheManager>();

            _shareManagerLogger = new Logger<ShareManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _objectManagerLogger = new Logger<ObjectsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _ddbFactoryLogger = new Logger<DdbManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _organizationsManagerLogger =
                new Logger<OrganizationsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _datasetsManagerLogger = new Logger<DatasetsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _batchTokenGeneratorLogger =
                new Logger<BatchTokenGenerator>(LoggerFactory.Create(builder => builder.AddConsole()));
            _nameGeneratorLogger = new Logger<NameGenerator>(LoggerFactory.Create(builder => builder.AddConsole()));
            
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
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            
            var manager = new ShareManager(_appSettingsMock.Object, _shareManagerLogger, _objectsManagerMock.Object,
                _datasetsManagerMock.Object,
                _organizationsManagerMock.Object, _utilsMock.Object, _authManagerMock.Object, 
                new BatchTokenGenerator(_appSettingsMock.Object, _batchTokenGeneratorLogger),
                new NameGenerator(_appSettingsMock.Object, _nameGeneratorLogger), GetTest1Context());

            await manager.Invoking(x => x.Initialize(null)).Should().ThrowAsync<BadRequestException>();
            
            // Now empty tag is supported
            //manager.Invoking(x => x.Initialize(new ShareInitDto())).Should().Throw<BadRequestException>();
            await manager.Invoking(x => x.Initialize(new ShareInitDto { Tag = "ciao" })).Should()
                .ThrowAsync<BadRequestException>();
        }*/

                [Test]
        public async Task Initialize_WithOrgWithoutDataset_GeneratedTag()
        {
            // INITIALIZATION & SETUP
            const string userName = "admin";

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();
            await using var appContext = GetAppTest1Context();

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
            settings.StoragePath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            var user = new User
            {
                UserName = userName,
                Email = "admin@example.com",
                Id = Guid.NewGuid().ToString()
            };
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(user));
            _authManagerMock.Setup(o => o.UserExists(user.Id)).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

            var attributes = new Dictionary<string, object>
            {
                { "public", true }
            };

            var ddbMock = new Mock<IDDB>();
            ddbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry
            {
                Properties = attributes
            }));
            
            var mockMeta = new MockMeta();
            ddbMock.Setup(x => x.Meta).Returns(mockMeta);

            var ddbMock2 = new Mock<IDDB>();
            ddbMock2.Setup(x => x.GetAttributesRaw()).Returns(attributes);
            ddbMock.Setup(x => x.GetAttributesAsync(default))
                .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);
            
            var ddbFactory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, 
                _appSettingsMock.Object, ddbFactory, webUtils, _authManagerMock.Object, _cacheManagerMock.Object,
                _fileSystem, _backgroundJobsProcessor);

            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
                _ddbFactoryMock.Object, _authManagerMock.Object);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils,
                datasetManager, appContext, _organizationsManagerLogger);

            var shareManager = new ShareManager(_appSettingsMock.Object, _shareManagerLogger, objectManager,
                datasetManager, organizationsManager, webUtils, _authManagerMock.Object, 
                new BatchTokenGenerator(_appSettingsMock.Object, _batchTokenGeneratorLogger), 
                new NameGenerator(_appSettingsMock.Object, _nameGeneratorLogger), context);

            // TEST 

            // ListBatches
            var batches =
                (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
                .ToArray();
            batches.Should().BeEmpty();

            // Initialize
            var initRes = await shareManager.Initialize(new ShareInitDto
            {
                OrgSlug = MagicStrings.PublicOrganizationSlug
            });

            initRes.Should().NotBeNull();
            initRes.Token.Should().NotBeNullOrWhiteSpace();

            // Commit
            var commitRes = await shareManager.Commit(initRes.Token);

            commitRes.Tag.OrganizationSlug.Should().Be(MagicStrings.PublicOrganizationSlug);

            // ListBatches
            batches = (await shareManager.ListBatches(commitRes.Tag.OrganizationSlug, commitRes.Tag.DatasetSlug))
                .ToArray();

            batches.Should().HaveCount(1);

            var batch = batches.First();

            batch.Token.Should().Be(initRes.Token);
            batch.UserName.Should().Be(userName);
            batch.End.Should().NotBeNull();
            batch.Status.Should().Be(BatchStatus.Committed);
            batch.Entries.Should().HaveCount(0);
        }
        
        [Test]
        public async Task Initialize_WithoutTag_GeneratedTag()
        {
            // INITIALIZATION & SETUP
            const string userName = "admin";

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();
            await using var appContext = GetAppTest1Context();

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
            settings.StoragePath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            var user = new User
            {
                UserName = userName,
                Email = "admin@example.com",
                Id = Guid.NewGuid().ToString()
            };
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(user));
            _authManagerMock.Setup(o => o.UserExists(user.Id)).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

            var attributes = new Dictionary<string, object>
            {
                { "public", true }
            };

            var ddbMock = new Mock<IDDB>();
            ddbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry
            {
                Properties = attributes
            }));
            
            var mockMeta = new MockMeta();
            ddbMock.Setup(x => x.Meta).Returns(mockMeta);

            var ddbMock2 = new Mock<IDDB>();
            ddbMock2.Setup(x => x.GetAttributesRaw()).Returns(attributes);
            ddbMock.Setup(x => x.GetAttributesAsync(default))
                .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);
            
            var ddbFactory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, 
                _appSettingsMock.Object, ddbFactory, webUtils, _authManagerMock.Object, _cacheManagerMock.Object,
                _fileSystem, _backgroundJobsProcessor);

            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
                _ddbFactoryMock.Object, _authManagerMock.Object);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils,
                datasetManager, appContext, _organizationsManagerLogger);

            var shareManager = new ShareManager(_appSettingsMock.Object, _shareManagerLogger, objectManager,
                datasetManager, organizationsManager, webUtils, _authManagerMock.Object, 
                new BatchTokenGenerator(_appSettingsMock.Object, _batchTokenGeneratorLogger), 
                new NameGenerator(_appSettingsMock.Object, _nameGeneratorLogger), context);

            // TEST 

            // ListBatches
            var batches =
                (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
                .ToArray();
            batches.Should().BeEmpty();

            // Initialize
            var initRes = await shareManager.Initialize(new ShareInitDto());

            initRes.Should().NotBeNull();
            initRes.Token.Should().NotBeNullOrWhiteSpace();
            //initRes.Tag.DatasetSlug.Should().Be(datasetTestSlug);
            //initRes.Tag.OrganizationSlug.Should().Be(userName);

            // ListBatches
            //batches = (await shareManager.ListBatches(initRes.Tag.OrganizationSlug, initRes.Tag.DatasetSlug)).ToArray();

            //batches.Should().HaveCount(1);

            //var batch = batches.First();

            //batch.UserName.Should().Be(userName);
            //batch.Status.Should().Be(BatchStatus.Running);
            //batch.End.Should().BeNull();

            // Commit
            var commitRes = await shareManager.Commit(initRes.Token);

            commitRes.Url.Should().Be($"/r/{commitRes.Tag.OrganizationSlug}/{commitRes.Tag.DatasetSlug}");

            // ListBatches
            batches = (await shareManager.ListBatches(commitRes.Tag.OrganizationSlug, commitRes.Tag.DatasetSlug))
                .ToArray();

            batches.Should().HaveCount(1);

            var batch = batches.First();

            batch.Token.Should().Be(initRes.Token);
            batch.UserName.Should().Be(userName);
            batch.End.Should().NotBeNull();
            batch.Status.Should().Be(BatchStatus.Committed);
            batch.Entries.Should().HaveCount(0);
        }
        
        [Test]
        public async Task EndToEnd_HappyPathRollback()
        {
            // INITIALIZATION & SETUP
            const string userName = "admin";

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();
            await using var appContext = GetAppTest1Context();

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
            settings.StoragePath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));

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
            ddbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry
            {
                Properties = attributes
            }));
            
            var mockMeta = new MockMeta();
            ddbMock.Setup(x => x.Meta).Returns(mockMeta);

            var ddbMock2 = new Mock<IDDB>();
            ddbMock2.Setup(x => x.GetAttributesRaw()).Returns(attributes);
            ddbMock.Setup(x => x.GetAttributesAsync(default))
                .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

            var ddbFactory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, 
                _appSettingsMock.Object, ddbFactory, webUtils, _authManagerMock.Object, _cacheManagerMock.Object,
                _fileSystem, _backgroundJobsProcessor);

            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
                _ddbFactoryMock.Object, _authManagerMock.Object);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils,
                datasetManager, appContext, _organizationsManagerLogger);

            var shareManager = new ShareManager(_appSettingsMock.Object, _shareManagerLogger, objectManager,
                datasetManager, organizationsManager, webUtils, _authManagerMock.Object, 
                new BatchTokenGenerator(_appSettingsMock.Object, _batchTokenGeneratorLogger), 
                new NameGenerator(_appSettingsMock.Object, _nameGeneratorLogger), context);

            // TEST

            const string fileName = "DJI_0028.JPG";
            const int fileSize = 3140384;
            const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
            const string organizationTestName = "test";
            const string datasetTestName = "First";
            const string organizationTestSlug = "test";
            const string datasetTestSlug = "first";
            const string newFileUrl =
                "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" +
                fileName;

            // ListBatches
            var batches =
                (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
                .ToArray();
            batches.Should().BeEmpty();

            var res = await organizationsManager.AddNew(new OrganizationDto
            {
                Name = organizationTestName,
                IsPublic = true,
                Slug = organizationTestSlug
            });

            res.Description.Should().BeNull();
            res.IsPublic.Should().BeTrue();
            res.Slug.Should().Be(organizationTestSlug);
            res.Name.Should().Be(organizationTestName);

            // Initialize
            var initRes = await shareManager.Initialize(new ShareInitDto
            {
                OrgSlug = organizationTestSlug,
                DsSlug = datasetTestSlug,
                DatasetName = datasetTestName
            });

            initRes.Should().NotBeNull();
            initRes.Token.Should().NotBeNullOrWhiteSpace();
            //initRes.Tag.DatasetSlug.Should().Be(datasetTestSlug);
            //initRes.Tag.OrganizationSlug.Should().Be(organizationTestSlug);

            // ListBatches
            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(1);

            var batch = batches.First();

            batch.UserName.Should().Be(userName);
            batch.Status.Should().Be(BatchStatus.Running);
            batch.End.Should().BeNull();

            // Upload
            var uploadRes =
                await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

            uploadRes.Path.Should().Be(fileName);
            uploadRes.Size.Should().Be(fileSize);
            uploadRes.Hash.Should().Be(fileHash);

            // Rollback
            await shareManager.Rollback(initRes.Token);

            // Check cleanup
            await datasetManager.Invoking(async o => await o.Get(organizationTestSlug, datasetTestSlug)).Should()
                .ThrowAsync<NotFoundException>();
        }

        [Test]
        public async Task EndToEnd_HappyPath()
        {
            // INITIALIZATION & SETUP
            const string userName = "admin";

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();
            await using var appContext = GetAppTest1Context();

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
            settings.StoragePath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));

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
            ddbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry
            {
                Properties = attributes
            }));

            var metaMock = new Mock<IMetaManager>();
            metaMock.Setup(x => x.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()));

            var mockMeta = new MockMeta();
            ddbMock.Setup(x => x.Meta).Returns(mockMeta);
            
            var ddbMock2 = new Mock<IDDB>();
            ddbMock2.Setup(x => x.GetAttributesRaw()).Returns(attributes);
            ddbMock.Setup(x => x.GetAttributesAsync(default))
                .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

            var ddbFactory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, 
                _appSettingsMock.Object, ddbFactory, webUtils, _authManagerMock.Object, _cacheManagerMock.Object,
                _fileSystem, _backgroundJobsProcessor);

            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
                _ddbFactoryMock.Object, _authManagerMock.Object);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils,
                datasetManager, appContext, _organizationsManagerLogger);

            var shareManager = new ShareManager(_appSettingsMock.Object, _shareManagerLogger, objectManager,
                datasetManager, organizationsManager, webUtils, _authManagerMock.Object, 
                new BatchTokenGenerator(_appSettingsMock.Object, _batchTokenGeneratorLogger), 
                new NameGenerator(_appSettingsMock.Object, _nameGeneratorLogger), context);

            // TEST

            const string fileName = "DJI_0028.JPG";
            const int fileSize = 3140384;
            const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
            const string organizationTestName = "test";
            const string datasetTestName = "First";
            const string organizationTestSlug = "test";
            const string datasetTestSlug = "first";
            const string newFileUrl =
                "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" +
                fileName;

            // ListBatches
            var batches =
                (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
                .ToArray();
            batches.Should().BeEmpty();

            var res = await organizationsManager.AddNew(new OrganizationDto
            {
                Name = organizationTestName,
                IsPublic = true,
                Slug = organizationTestSlug
            });

            res.Description.Should().BeNull();
            res.IsPublic.Should().BeTrue();
            res.Slug.Should().Be(organizationTestSlug);
            res.Name.Should().Be(organizationTestName);

            // Initialize
            var initRes = await shareManager.Initialize(new ShareInitDto
            {
                DsSlug = datasetTestSlug,
                OrgSlug = organizationTestSlug,
                DatasetName = datasetTestName
            });

            initRes.Should().NotBeNull();
            initRes.Token.Should().NotBeNullOrWhiteSpace();
            //initRes.Tag.DatasetSlug.Should().Be(datasetTestSlug);
            //initRes.Tag.OrganizationSlug.Should().Be(organizationTestSlug);

            // ListBatches
            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(1);

            var batch = batches.First();

            batch.UserName.Should().Be(userName);
            batch.Status.Should().Be(BatchStatus.Running);
            batch.End.Should().BeNull();

            // Upload
            var uploadRes =
                await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

            uploadRes.Path.Should().Be(fileName);
            uploadRes.Size.Should().Be(fileSize);
            uploadRes.Hash.Should().Be(fileHash);

            // Commit
            var commitRes = await shareManager.Commit(initRes.Token);

            commitRes.Url.Should().Be($"/r/{organizationTestSlug}/{datasetTestSlug}");
            commitRes.Tag.OrganizationSlug.Should().Be(organizationTestSlug);
            commitRes.Tag.DatasetSlug.Should().Be(datasetTestSlug);

            // ListBatches
            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(1);

            batch = batches.First();

            batch.Token.Should().Be(initRes.Token);
            batch.UserName.Should().Be(userName);
            batch.End.Should().NotBeNull();
            batch.Status.Should().Be(BatchStatus.Committed);
            batch.Entries.Should().HaveCount(1);
        }

        [Test]
        public async Task EndToEnd_ShareInit_After_ShareInit()
        {
            // INITIALIZATION & SETUP 
            const string userName = "admin";

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();
            await using var appContext = GetAppTest1Context();
            
            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
            settings.StoragePath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);

            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));
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
            ddbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry
            {
                Properties = attributes
            }));
            
            var mockMeta = new MockMeta();
            ddbMock.Setup(x => x.Meta).Returns(mockMeta);

            var ddbMock2 = new Mock<IDDB>();
            ddbMock2.Setup(x => x.GetAttributesRaw()).Returns(attributes);
            ddbMock.Setup(x => x.GetAttributesAsync(default))
                .Returns(Task.FromResult(new EntryAttributes(ddbMock2.Object)));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

            var ddbFactory = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, 
                _appSettingsMock.Object, ddbFactory, webUtils, _authManagerMock.Object, _cacheManagerMock.Object,
                _fileSystem, _backgroundJobsProcessor);

            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager,
                _ddbFactoryMock.Object, _authManagerMock.Object);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils,
                datasetManager, appContext, _organizationsManagerLogger);

            var shareManager = new ShareManager(_appSettingsMock.Object, _shareManagerLogger, objectManager,
                datasetManager, organizationsManager, webUtils, _authManagerMock.Object, 
                new BatchTokenGenerator(_appSettingsMock.Object, _batchTokenGeneratorLogger), 
                new NameGenerator(_appSettingsMock.Object, _nameGeneratorLogger), context);

            // TEST 

            const string fileName = "DJI_0028.JPG";
            const int fileSize = 3140384;
            const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
            const string organizationTestName = "test";
            const string datasetTestName = "First";
            const string organizationTestSlug = "test";
            const string datasetTestSlug = "first";
            const string newFileUrl =
                "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" +
                fileName;

            // ListBatches
            var batches =
                (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug))
                .ToArray();
            batches.Should().BeEmpty();

            await organizationsManager.AddNew(new OrganizationDto
            {
                Name = organizationTestName,
                IsPublic = true,
                Slug = organizationTestSlug
            });

            // Initialize
            var initRes = await shareManager.Initialize(new ShareInitDto
            {
                DsSlug = datasetTestSlug,
                OrgSlug = organizationTestSlug,
                DatasetName = datasetTestName,
            });

            initRes.Should().NotBeNull();
            initRes.Token.Should().NotBeNullOrWhiteSpace();
            //initRes.Tag.DatasetSlug.Should().Be(datasetTestSlug);
            //initRes.Tag.OrganizationSlug.Should().Be(organizationTestSlug);

            // ListBatches
            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(1);


            // Upload
            await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

            // Initialize
            var newInitRes = await shareManager.Initialize(new ShareInitDto
            {
                DsSlug = datasetTestSlug,
                OrgSlug = organizationTestSlug,
                DatasetName = datasetTestName,
            });

            initRes.Token.Should().NotBeNullOrWhiteSpace();

            // ListBatches
            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(2);

            var oldBatch = batches.FirstOrDefault(item => item.Token == initRes.Token);
            oldBatch.Should().NotBeNull();

            // ReSharper disable once PossibleNullReferenceException
            oldBatch.End.Should().NotBeNull();
            oldBatch.Status.Should().Be(BatchStatus.Rolledback);

            var newBatch = batches.FirstOrDefault(item => item.Token == newInitRes.Token);
            newBatch.Should().NotBeNull();

            // ReSharper disable once PossibleNullReferenceException
            newBatch.End.Should().BeNull();
            newBatch.Status.Should().Be(BatchStatus.Running);

            // Upload to old batch -> Exception
            await shareManager.Invoking(async x =>
                    await x.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl)))
                .Should().ThrowAsync<BadRequestException>();

            // Commit old batch -> Exception
            await shareManager.Invoking(async x => await shareManager.Commit(initRes.Token))
                .Should().ThrowAsync<BadRequestException>();

            // Fix
            context.Set<Data.Models.Entry>().Local.Clear();

            // Upload to new batch
            var uploadRes =
                await shareManager.Upload(newInitRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));
            uploadRes.Path.Should().Be(fileName);
            uploadRes.Size.Should().Be(fileSize);
            uploadRes.Hash.Should().Be(fileHash);

            // Commit
            var commitRes = await shareManager.Commit(newInitRes.Token);

            commitRes.Url.Should().Be($"/r/{organizationTestSlug}/{datasetTestSlug}");
            commitRes.Tag.OrganizationSlug.Should().Be(organizationTestSlug);
            commitRes.Tag.DatasetSlug.Should().Be(datasetTestSlug);

            // ListBatches
            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(2);

            //batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            //batches.Should().HaveCount(1);
            //var batch = batches.First();
            //batch.Token.Should().Be(token);
            //batch.UserName.Should().Be("admin");
            //batch.Entries.Should().HaveCount(1);
            //batch.End.Should().NotBeNull();
        }


        #region Test Data

        private readonly string _settingsJson = @"{
    ""Secret"": ""a2780070a24cfcaf5a4a43f931200ba0d19d8b86b3a7bd5123d9ad75b125f480fcce1f9b7f41a53abe2ba8456bd142d38c455302e0081e5139bc3fc9bf614497"",
    ""TokenExpirationInDays"": 7,
    ""RevokedTokens"": [
      """"
    ],
    ""AuthProvider"": ""Sqlite"",
    ""RegistryProvider"": ""Sqlite"",
    ""DefaultAdmin"": {
      ""Email"": ""admin@example.com"",
      ""UserName"": ""admin"",
      ""Password"": ""password""
    },
    ""StoragePath"": ""./Data/Ddb"",
    ""DdbPath"": """",
    ""MaxUploadChunkSize"": 512000,
    ""MaxRequestBodySize"": 52428800,
    ""BatchTokenLength"": 32,
    ""RandomDatasetNameLength"": 16 
}
  ";

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
        
        private static ApplicationDbContext GetAppTest1Context()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "RegistryAppDatabase-" + Guid.NewGuid())
                .Options;

            // Insert seed data into the database using one instance of the context
            using (var context = new ApplicationDbContext(options))
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
            }

            return new ApplicationDbContext(options);
        }


        #endregion
    }
}