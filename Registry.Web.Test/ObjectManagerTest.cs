using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.CodeAnalysis.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
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
    [TestFixture]
    public class ObjectManagerTest
    {
        private Logger<DdbFactory> _ddbFactoryLogger;
        private Logger<ObjectsManager> _objectManagerLogger;
        private Mock<IObjectSystem> _objectSystemMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IDdbFactory> _ddbFactoryMock;
        private Mock<IAuthManager> _authManagerMock;
        private Mock<IUtils> _utilsMock;

        private const string DataFolder = "Data";
        private const string TestStorageFolder = @"Data/Storage";
        private const string DdbTestDataFolder = @"Data/DdbTest";
        private const string StorageFolder = "Storage";
        private const string DdbFolder = "Ddb";

        private const string BaseTestFolder = "ObjectManagerTest";

        private const string Test1ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/Test1.zip";

        [SetUp]
        public void Setup()
        {
            _objectSystemMock = new Mock<IObjectSystem>();
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryMock = new Mock<IDdbFactory>();
            _authManagerMock = new Mock<IAuthManager>();
            _utilsMock = new Mock<IUtils>();

            if (!Directory.Exists(TestStorageFolder))
                Directory.CreateDirectory(TestStorageFolder);

            if (!Directory.Exists(DdbTestDataFolder))
            {
                Directory.CreateDirectory(DdbTestDataFolder);
                File.WriteAllText(Path.Combine(DdbTestDataFolder, "ddbcmd.exe"), string.Empty);
            }

            _ddbFactoryLogger = new Logger<DdbFactory>(LoggerFactory.Create(builder => builder.AddConsole()));
            _objectManagerLogger = new Logger<ObjectsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
        }

        [Test]
        public void List_NullParameters_BadRequestException()
        {
            using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _objectSystemMock.Object, _appSettingsMock.Object,
                _ddbFactoryMock.Object, new WebUtils(_authManagerMock.Object, context));

            objectManager.Invoking(item => item.List(null, MagicStrings.DefaultDatasetSlug, "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List(MagicStrings.PublicOrganizationSlug, null, "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List(string.Empty, MagicStrings.DefaultDatasetSlug, "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List(MagicStrings.PublicOrganizationSlug, string.Empty, "test")).Should().Throw<BadRequestException>();
        }

        [Test]
        public async Task List_PublicDefault_ListObjects()
        {
            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);
            await using var context = GetTest1Context();

            _settings.DdbStoragePath = Path.Combine(test.TestFolder, DdbFolder);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _objectSystemMock.Object, _appSettingsMock.Object,
                new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger), new WebUtils(_authManagerMock.Object, context));

            var res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, null);

            res.Should().HaveCount(26);

            res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "Sub");

            res.Should().HaveCount(8);

        }

        [Test]
        public async Task Get_MissingFile_NotFound()
        {

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);
            await using var context = GetTest1Context();

            _settings.DdbStoragePath = Path.Combine(test.TestFolder, DdbFolder);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var objectManager = new ObjectsManager(_objectManagerLogger, context, new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder)), _appSettingsMock.Object,
                new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger), new WebUtils(_authManagerMock.Object, context));

            objectManager.Invoking(async x => await x.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "weriufbgeiughegr"))
                .Should().Throw<NotFoundException>();

        }

        [Test]
        public async Task Get_ExistingFile_FileRes()
        {
            var expectedHash = new byte[] { 152, 110, 79, 250, 177, 15, 101, 187, 24, 23, 34, 217, 117, 168, 119, 124 };

            const string expectedName = "DJI_0019.JPG";
            const EntryType expectedObjectType = EntryType.GeoImage;
            const string expectedContentType = "image/jpeg";

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            await using var context = GetTest1Context();
            _settings.DdbStoragePath = Path.Combine(test.TestFolder, DdbFolder);
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var sys = new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder));
            sys.SyncBucket($"{MagicStrings.PublicOrganizationSlug}-{MagicStrings.DefaultDatasetSlug}");

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys, _appSettingsMock.Object,
                new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger), new WebUtils(_authManagerMock.Object, context));

            var obj = await objectManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "DJI_0019.JPG");

            obj.Name.Should().Be(expectedName);
            obj.Type.Should().Be(expectedObjectType);
            obj.ContentType.Should().Be(expectedContentType);
            MD5.Create().ComputeHash(obj.Data).Should().BeEquivalentTo(expectedHash);
            
        }

        
        [Test]
        public async Task AddNew_File_FileRes()
        {
            
            const string fileName = "DJI_0028.JPG";

            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            using var test = new TestFS(Test1ArchiveUrl, BaseTestFolder);

            var sys = new PhysicalObjectSystem(Path.Combine(test.TestFolder, StorageFolder));
            sys.SyncBucket($"{MagicStrings.PublicOrganizationSlug}-{MagicStrings.DefaultDatasetSlug}");

            var objectManager = new ObjectsManager(_objectManagerLogger, context, sys, _appSettingsMock.Object,
                new DdbFactory(_appSettingsMock.Object, _ddbFactoryLogger), new WebUtils(_authManagerMock.Object, context));

            var res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName);

            res.Should().HaveCount(1);

            await objectManager.Delete(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileName);

            res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName);

            res.Should().HaveCount(0);
            
            var newFileUrl = "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" + fileName;

            var ret = await objectManager.AddNew(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName, CommonUtils.SmartDownloadData(newFileUrl));

            res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName);

            res.Should().HaveCount(1);

            // TODO: Should check why geometry is null

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
    ""DdbPath"": ""./ddb"",
""SupportedDdbVersion"": {
      ""Major"": 0,
      ""Minor"": 9,
      ""Build"": 3
    }
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
