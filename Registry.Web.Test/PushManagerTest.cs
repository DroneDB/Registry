using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Adapters.ObjectSystem;
using Registry.Adapters.ObjectSystem.Model;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;
using Registry.Ports.ObjectSystem;
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

namespace Registry.Web.Test
{
    class PushManagerTest
    {
        private Logger<PushManager> _pushManagerLogger;
        private Logger<ObjectsManager> _objectManagerLogger;
        private Logger<DdbManager> _ddbFactoryLogger;
        private Logger<DatasetsManager> _datasetsManagerLogger;
        private Logger<OrganizationsManager> _organizationsManagerLogger;
        private Logger<BatchTokenGenerator> _batchTokenGeneratorLogger;
        private Logger<NameGenerator> _nameGeneratorLogger;

        private IPasswordHasher _passwordHasher;

        private Mock<IObjectSystem> _objectSystemMock;
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

        private INameGenerator _nameGenerator;
        private IBatchTokenGenerator _batchTokenGenerator;

        private const string TestStorageFolder = @"Data/Storage";
        //private const string DdbTestDataFolder = @"Data/DdbTest";
        private const string StorageFolder = "Storage";
        private const string DdbFolder = "Ddb";

        private const string BaseTestFolder = "PushManagerTest";


        private const string Test2ArchiveDatasetInternalGuid = "496af2f3-8c6c-41c2-95b9-dd2846e66d95";

        private const string TestArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/push/test.zip";

        public PushManagerTest()
        {
            //
        }

        [SetUp]
        public void Setup()
        {
            _objectSystemMock = new Mock<IObjectSystem>();
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryMock = new Mock<IDdbManager>();
            _authManagerMock = new Mock<IAuthManager>();
            _utilsMock = new Mock<IUtils>();

            _objectsManagerMock = new Mock<IObjectsManager>();
            _organizationsManagerMock = new Mock<IOrganizationsManager>();
            _datasetsManagerMock = new Mock<IDatasetsManager>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();

            _cacheManagerMock = new Mock<ICacheManager>();
            _passwordHasher = new PasswordHasher();

            _pushManagerLogger = new Logger<PushManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _objectManagerLogger = new Logger<ObjectsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _ddbFactoryLogger = new Logger<DdbManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _organizationsManagerLogger = new Logger<OrganizationsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _datasetsManagerLogger = new Logger<DatasetsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _batchTokenGeneratorLogger = new Logger<BatchTokenGenerator>(LoggerFactory.Create(builder => builder.AddConsole()));
            _nameGeneratorLogger = new Logger<NameGenerator>(LoggerFactory.Create(builder => builder.AddConsole()));

            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _batchTokenGenerator = new BatchTokenGenerator(_appSettingsMock.Object, _batchTokenGeneratorLogger);
            _nameGenerator = new NameGenerator(_appSettingsMock.Object, _nameGeneratorLogger);
            _backgroundJobsProcessor = new SimpleBackgroundJobsProcessor();

            var ddbMock1 = new Mock<IDdb>();
            ddbMock1.Setup(x => x.GetAttributesRaw()).Returns(new Dictionary<string, object>
            {
                {"public", true }
            });
            var ddbMock2 = new Mock<IDdb>();
            ddbMock2.Setup(x => x.GetAttributes()).Returns(new DdbAttributes(ddbMock1.Object));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock2.Object);

        }

        [Test]
        public async Task Init_HappyPath_Ok()
        {

            /* INITIALIZATION & SETUP */
            const string userName = "admin";
            const string dsSlug = "test";

            using var test = new TestFS(TestArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();

            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
            {
                UserName = userName,
                Email = "admin@example.com"
            }));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));

            var sys = new PhysicalObjectSystem(new PhysicalObjectSystemSettings { BasePath = Path.Combine(test.TestFolder, StorageFolder) });
            sys.SyncBucket($"{userName}-{Test2ArchiveDatasetInternalGuid}");

            var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys,
                _appSettingsMock.Object, ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _backgroundJobsProcessor);

            var pushManager = new PushManager(webUtils, ddbManager, sys, objectManager, _pushManagerLogger,
                _datasetsManagerMock.Object, _authManagerMock.Object, _backgroundJobsProcessor, _appSettingsMock.Object);

            try
            {

                await using (var stream = File.OpenRead(Path.Combine(test.TestFolder, "ClientDdb.zip")))
                {
                    var result = await pushManager.Init(userName, dsSlug, stream);

                    TestContext.WriteLine(JsonConvert.SerializeObject(result));

                    foreach (var file in result.NeededFiles)
                    {
                        var filePath = Path.Combine(test.TestFolder, "NewFiles", file);
                        if (!File.Exists(filePath))
                        {
                            Assert.Fail($"File '{file}' not present in test archive");
                            return;
                        }

                        await using var up = File.OpenRead(filePath);
                        await pushManager.Upload(userName, dsSlug, file, up);
                    }

                }

                await pushManager.Commit(userName, dsSlug);

                // Verify that all the files are in the correct places
                await using (var stream = File.OpenRead(Path.Combine(test.TestFolder, "ClientDdb.zip")))
                {
                    var result = await pushManager.Init(userName, dsSlug, stream);

                    TestContext.WriteLine(JsonConvert.SerializeObject(result));

                    result.NeededFiles.Should().BeEmpty();

                }
            }
            finally
            {
                await pushManager.Clean(userName, dsSlug);
            }

        }

        [Test]
        [Explicit]
        public async Task Init_HappyPath2_Ok()
        {

            /* INITIALIZATION & SETUP */
            const string userName = "admin";
            const string dsSlug = "test";

            using var test = new TestFS(TestArchiveUrl, BaseTestFolder, true);

            await using var context = GetTest1Context();

            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
            {
                UserName = userName,
                Email = "admin@example.com"
            }));
            _authManagerMock.Setup(o => o.SafeGetCurrentUserName()).Returns(Task.FromResult(userName));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));

            var sys = new S3ObjectSystem(new S3ObjectSystemSettings
            {
                AccessKey = "minioadmin",
                SecretKey = "minioadmin",
                AppName = "Registry",
                AppVersion = "1.0",
                Endpoint = "localhost:9000",
                Region = "us-east-1",
                UseSsl = false
            });

            var bucketName = userName + "-496af2f3-8c6c-41c2-95b9-dd2846e66d95";

            if (await sys.BucketExistsAsync(bucketName))
                await sys.RemoveBucketAsync(bucketName);

            await sys.MakeBucketAsync(bucketName);

            var basePath = Path.Combine(test.TestFolder,
                "Storage/admin-496af2f3-8c6c-41c2-95b9-dd2846e66d95");

            foreach (var entry in Directory.EnumerateFiles(basePath, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(basePath, entry).Replace('\\', '/');
                TestContext.WriteLine(relPath);
                await sys.PutObjectAsync(bucketName, relPath, entry);
            }

            //var sys = new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder));
            //sys.SyncBucket($"{userName}-{Test2ArchiveDatasetInternalGuid}");

            var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);
            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys,
                _appSettingsMock.Object, ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _backgroundJobsProcessor);

            var pushManager = new PushManager(webUtils, ddbManager, sys, objectManager, _pushManagerLogger,
                _datasetsManagerMock.Object, _authManagerMock.Object, _backgroundJobsProcessor, _appSettingsMock.Object);

            try
            {

                await using (var stream = File.OpenRead(Path.Combine(test.TestFolder, "ClientDdb.zip")))
                {
                    var result = await pushManager.Init(userName, dsSlug, stream);

                    TestContext.WriteLine(JsonConvert.SerializeObject(result));

                    foreach (var file in result.NeededFiles)
                    {
                        var filePath = Path.Combine(test.TestFolder, "NewFiles", file);
                        if (!File.Exists(filePath))
                        {
                            Assert.Fail($"File '{file}' not present in test archive");
                            return;
                        }

                        await using var up = File.OpenRead(filePath);
                        await pushManager.Upload(userName, dsSlug, file, up);
                    }

                }

                await pushManager.Commit(userName, dsSlug);

                // Verify that all the files are in the correct places
                await using (var stream = File.OpenRead(Path.Combine(test.TestFolder, "ClientDdb.zip")))
                {
                    var result = await pushManager.Init(userName, dsSlug, stream);

                    TestContext.WriteLine(JsonConvert.SerializeObject(result));

                    result.NeededFiles.Should().BeEmpty();

                }

            }
            finally
            {
                await pushManager.Clean(userName, dsSlug);
            }

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
    ""DdbStoragePath"": ""./Ddb"",
    ""DdbPath"": """",
    ""MaxUploadChunkSize"": 512000,
    ""MaxRequestBodySize"": 52428800,
    ""SupportedDdbVersion"": {
      ""Major"": 0,
      ""Minor"": 9,
      ""Build"": 13
    },
    ""BatchTokenLength"": 32,
    ""RandomDatasetNameLength"": 16 
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
                    Name = "Default",
                    Description = "Default dataset",
                    //IsPublic = true,
                    CreationDate = DateTime.Now,
                    //LastUpdate = DateTime.Now,
                    InternalRef = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75")
                };

                entity.Datasets = new List<Dataset> { ds };

                context.Organizations.Add(entity);

                // Insert seed data into the database using one instance of the context
                entity = new Organization
                {
                    Slug = "admin",
                    Name = "Admin",
                    CreationDate = DateTime.Now,
                    Description = "Admin organization",
                    IsPublic = false,
                    OwnerId = null
                };
                ds = new Dataset
                {
                    Slug = "test",
                    Name = "Test",
                    Description = "Test dataset",
                    //IsPublic = false,
                    CreationDate = DateTime.Now,
                    //LastUpdate = DateTime.Now,
                    InternalRef = Guid.Parse(Test2ArchiveDatasetInternalGuid)
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
