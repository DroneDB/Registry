using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Adapters.ObjectSystem;
using Registry.Common;
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    class ShareManagerTest
    {
        private Logger<ShareManager> _shareManagerLogger;
        private Logger<ObjectsManager> _objectManagerLogger;
        private Logger<DdbFactory> _ddbFactoryLogger;
        private Logger<DatasetsManager> _datasetsManagerLogger;
        private Logger<OrganizationsManager> _organizationsManagerLogger;

        private IPasswordHasher _passwordHasher;

        private Mock<IObjectSystem> _objectSystemMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IDdbFactory> _ddbFactoryMock;
        private Mock<IAuthManager> _authManagerMock;
        private Mock<IUtils> _utilsMock;
        private Mock<IObjectsManager> _objectsManagerMock;
        private Mock<IOrganizationsManager> _organizationsManagerMock;
        private Mock<IDatasetsManager> _datasetsManagerMock;

        private const string TestStorageFolder = @"Data/Storage";
        //private const string DdbTestDataFolder = @"Data/DdbTest";
        private const string StorageFolder = "Storage";
        private const string DdbFolder = "Ddb";

        private const string BaseTestFolder = "ShareManagerTest";

        private const string Test1ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/Test1.zip";


        public ShareManagerTest()
        {
            //
        }

        [SetUp]
        public void Setup()
        {
            _objectSystemMock = new Mock<IObjectSystem>();
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryMock = new Mock<IDdbFactory>();
            _authManagerMock = new Mock<IAuthManager>();
            _utilsMock = new Mock<IUtils>();

            _objectsManagerMock = new Mock<IObjectsManager>();
            _organizationsManagerMock = new Mock<IOrganizationsManager>();
            _datasetsManagerMock = new Mock<IDatasetsManager>();

            _passwordHasher = new PasswordHasher();
            
            _shareManagerLogger = new Logger<ShareManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _objectManagerLogger = new Logger<ObjectsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _ddbFactoryLogger = new Logger<DdbFactory>(LoggerFactory.Create(builder => builder.AddConsole()));
            _organizationsManagerLogger = new Logger<OrganizationsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _datasetsManagerLogger = new Logger<DatasetsManager>(LoggerFactory.Create(builder => builder.AddConsole()));

        }

        [Test]
        public void Initialize_NullParameters_BadRequest()
        {
            const string userName = "admin";

            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
            {
                UserName = userName,
                Email = "admin@example.com"
            }));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));


            var manager = new ShareManager(_shareManagerLogger, _objectsManagerMock.Object, _datasetsManagerMock.Object,
                _organizationsManagerMock.Object, _utilsMock.Object, _authManagerMock.Object, GetTest1Context());

            manager.Invoking(x => x.Initialize(null)).Should().Throw<BadRequestException>();
            // Now empty tag is supported
            //manager.Invoking(x => x.Initialize(new ShareInitDto())).Should().Throw<BadRequestException>();
            manager.Invoking(x => x.Initialize(new ShareInitDto { Tag = "ciao"})).Should().Throw<BadRequestException>();

        }

        [Test]
        public async Task Initialize_WithoutTag_GeneratedTag()
        {
            /* INITIALIZATION & SETUP */
            const string userName = "admin";

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();

            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
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

            var sys = new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder));
            sys.SyncBucket($"{MagicStrings.PublicOrganizationSlug}-{MagicStrings.DefaultDatasetSlug}");

            var ddbFactory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys, _appSettingsMock.Object, ddbFactory, webUtils);

            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager, ddbFactory, _passwordHasher);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils, datasetManager, _organizationsManagerLogger);

            var shareManager = new ShareManager(_shareManagerLogger, objectManager, datasetManager, organizationsManager, webUtils, _authManagerMock.Object, context);

            /* TEST */

            // ListBatches
            var batches = (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).ToArray();
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
            batches = (await shareManager.ListBatches(commitRes.Tag.OrganizationSlug, commitRes.Tag.DatasetSlug)).ToArray();

            batches.Should().HaveCount(1);

            var batch = batches.First();

            batch.Token.Should().Be(initRes.Token);
            batch.UserName.Should().Be(userName);
            batch.End.Should().NotBeNull();
            batch.Status.Should().Be(BatchStatus.Committed);
            batch.Entries.Should().HaveCount(0);
        }

        [Test] 
        public async Task EndToEnd_HappyPath()
        {
            /* INITIALIZATION & SETUP */
            const string userName = "admin";

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder, true);
            
            await using var context = GetTest1Context();

            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
            {
                UserName = userName,
                Email = "admin@example.com"
            }));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));
            
            var sys = new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder));
            sys.SyncBucket($"{MagicStrings.PublicOrganizationSlug}-{MagicStrings.DefaultDatasetSlug}");

            var ddbFactory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys, _appSettingsMock.Object, ddbFactory, webUtils);
           
            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager, ddbFactory, _passwordHasher);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils, datasetManager, _organizationsManagerLogger);

            var shareManager = new ShareManager(_shareManagerLogger, objectManager, datasetManager, organizationsManager, webUtils, _authManagerMock.Object, context);

            /* TEST */

            const string fileName = "DJI_0028.JPG";
            const int fileSize = 3140384;
            const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
            const string organizationTestName = "test";
            const string datasetTestName = "First";
            const string organizationTestSlug = "test";
            const string datasetTestSlug = "first";
            const string testPassword = "ciaoatutti";
            const string newFileUrl = "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" + fileName;

            // ListBatches
            var batches = (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).ToArray();
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
                Tag = $"{organizationTestSlug}/{datasetTestSlug}",
                DatasetName = datasetTestName,
                Password = testPassword
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
            var uploadRes = await shareManager.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

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

            /* INITIALIZATION & SETUP */
            const string userName = "admin";

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();

            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
            {
                UserName = userName,
                Email = "admin@example.com"
            }));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));

            var sys = new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder));
            sys.SyncBucket($"{MagicStrings.PublicOrganizationSlug}-{MagicStrings.DefaultDatasetSlug}");

            var ddbFactory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys, _appSettingsMock.Object, ddbFactory, webUtils);

            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager, ddbFactory, _passwordHasher);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils, datasetManager, _organizationsManagerLogger);

            var shareManager = new ShareManager(_shareManagerLogger, objectManager, datasetManager, organizationsManager, webUtils, _authManagerMock.Object, context);

            /* TEST */

            const string fileName = "DJI_0028.JPG";
            const int fileSize = 3140384;
            const string fileHash = "7cf58d0a06c56092aa5d6e108e385ad942225c75b462406cdf50d66f829572d3";
            const string organizationTestName = "test";
            const string datasetTestName = "First";
            const string organizationTestSlug = "test";
            const string datasetTestSlug = "first";
            const string testPassword = "ciaoatutti";
            const string newFileUrl = "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" + fileName;

            // ListBatches
            var batches = (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).ToArray();
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
                Tag = $"{organizationTestSlug}/{datasetTestSlug}",
                DatasetName = datasetTestName,
                Password = testPassword
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
                Tag = $"{organizationTestSlug}/{datasetTestSlug}",
                DatasetName = datasetTestName,
                Password = testPassword
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
            shareManager.Invoking(async x => await x.Upload(initRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl)))
                .Should().Throw<BadRequestException>();

            // Commit old batch -> Exception
            shareManager.Invoking(async x => await shareManager.Commit(initRes.Token))
                .Should().Throw<BadRequestException>();

            // Fix
            context.Set<Entry>().Local.Clear();
            
            // Upload to new batch
            var uploadRes = await shareManager.Upload(newInitRes.Token, fileName, CommonUtils.SmartDownloadData(newFileUrl));
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
    ""DdbPath"": """",
""SupportedDdbVersion"": {
      ""Major"": 0,
      ""Minor"": 9,
      ""Build"": 3
    }
}
  ");

        public ShareManagerTest(IPasswordHasher passwordHasher)
        {
            this._passwordHasher = passwordHasher;
        }

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
                    Name = "Default",
                    Description = "Default dataset",
                    IsPublic = true,
                    CreationDate = DateTime.Now,
                    LastEdit = DateTime.Now
                };
                entity.Datasets = new List<Dataset> { ds };

                context.Organizations.Add(entity);

                context.SaveChanges();
            }

            return new RegistryContext(options);
        }

        #endregion


    }
}
