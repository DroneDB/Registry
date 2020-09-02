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

            //if (!Directory.Exists(TestStorageFolder))
            //    Directory.CreateDirectory(TestStorageFolder);

            //if (!Directory.Exists(DdbTestDataFolder))
            //{
            //    Directory.CreateDirectory(DdbTestDataFolder);
            //    File.WriteAllText(Path.Combine(DdbTestDataFolder, "ddbcmd.exe"), string.Empty);
            //}

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

            var manager = new ShareManager(_shareManagerLogger, _objectsManagerMock.Object, _datasetsManagerMock.Object,
                _organizationsManagerMock.Object, _utilsMock.Object, _authManagerMock.Object, GetTest1Context());

            manager.Invoking(x => x.Initialize(null)).Should().Throw<BadRequestException>();
            manager.Invoking(x => x.Initialize(new ShareInitDto())).Should().Throw<BadRequestException>();
            manager.Invoking(x => x.Initialize(new ShareInitDto { OrganizationName = "Test", OrganizationSlug = "test"})).Should().Throw<BadRequestException>();
            manager.Invoking(x => x.Initialize(new ShareInitDto { DatasetName = "Test", DatasetSlug = "test" })).Should().Throw<BadRequestException>();

        }

        [Test]
        [Ignore]
        public async Task EndToEnd_HappyPath()
        {

            const string fileName = "DJI_0028.JPG";

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);
            await using var context = GetTest1Context();

            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
            {
                UserName = "admin",
                Email = "admin@example.com"
            }));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult("admin"));
            
            var sys = new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder));
            sys.SyncBucket($"{MagicStrings.PublicOrganizationSlug}-{MagicStrings.DefaultDatasetSlug}");

            var ddbFactory = new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys, _appSettingsMock.Object, ddbFactory, webUtils);
           
            var datasetManager = new DatasetsManager(context, webUtils, _datasetsManagerLogger, objectManager, ddbFactory, _passwordHasher);
            var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, webUtils, datasetManager, _organizationsManagerLogger);

            var shareManager = new ShareManager(_shareManagerLogger, objectManager, datasetManager, organizationsManager, webUtils, _authManagerMock.Object, context);

            var batches = (await shareManager.ListBatches(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).ToArray();

            batches.Should().BeEmpty();

            const string organizationTestName = "Test";
            const string datasetTestName = "First";
            const string organizationTestSlug = "test";
            const string datasetTestSlug = "first";

            const string testPassword = "ciaoatutti";

            // Initialize
            var token = await shareManager.Initialize(new ShareInitDto
            {
                DatasetName = datasetTestName,
                OrganizationName = organizationTestName,
                Password = testPassword
            });

            token.Should().NotBeNullOrWhiteSpace();

            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(1);

            var newFileUrl = "https://github.com/pierotofy/drone_dataset_brighton_beach/raw/master/" + fileName;

            await shareManager.Upload(token, fileName, CommonUtils.SmartDownloadData(newFileUrl));

            await shareManager.Commit(token);

            batches = (await shareManager.ListBatches(organizationTestSlug, datasetTestSlug)).ToArray();

            batches.Should().HaveCount(1);
            var batch = batches.First();
            batch.Token.Should().Be(token);
            batch.UserName.Should().Be("admin");
            batch.Entries.Should().BeEmpty();
            batch.End.Should().NotBeNull();

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
