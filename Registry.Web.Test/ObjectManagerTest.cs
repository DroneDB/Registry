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
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Adapters.ObjectSystem;
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
    public class ObjectManagerTest
    {
        private Mock<ILogger<ObjectsManager>> _loggerMock;
        private Mock<IObjectSystem> _objectSystemMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IDdbFactory> _ddbFactoryMock;
        private Mock<IAuthManager> _authManagerMock;
        private Mock<IUtils> _utilsMock;

        private const string TestDataFolder = @"Data/Ddb";
        private const string TestStorageFolder = @"Data/Storage";
        private const string TestDatasetFolder = "public-default";

        [OneTimeSetUp]
        public void TestSetup()
        {
            var cli = new WebClient();
            var temp = Path.GetTempFileName();

            var destFolder = Path.Combine(TestStorageFolder, TestDatasetFolder);
            if (!Directory.Exists(destFolder))
                Directory.CreateDirectory(destFolder);

            var count = Directory.EnumerateFiles(destFolder);

            if (count.Count() == 19)
            {
                Debug.WriteLine("Test dataset already existing, nothing to do");
                return;
            }

            Debug.WriteLine("Downloading test dataset");

            cli.DownloadFile("https://github.com/pierotofy/drone_dataset_brighton_beach/archive/master.zip", temp);

            Debug.WriteLine("Extracting test dataset");

            ZipFile.ExtractToDirectory(temp, TestStorageFolder, true);

            foreach (var file in Directory.EnumerateFiles(Path.Combine(TestStorageFolder, "drone_dataset_brighton_beach-master")))
                File.Move(file, Path.Combine(destFolder, Path.GetFileName(file)));

            Directory.Delete(Path.Combine(TestStorageFolder, "drone_dataset_brighton_beach-master"));

            File.Delete(temp);

            Debug.WriteLine("Test data is ready");

        }

        [SetUp]
        public void Setup()
        {
            _loggerMock = new Mock<ILogger<ObjectsManager>>();
            _objectSystemMock = new Mock<IObjectSystem>();
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryMock = new Mock<IDdbFactory>();
            _authManagerMock = new Mock<IAuthManager>();
            _utilsMock = new Mock<IUtils>();

            if (!Directory.Exists(TestStorageFolder))
                Directory.CreateDirectory(TestStorageFolder);
        }

        [Test]
        public void List_NullParameters_BadRequestException()
        {
            using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var objectManager = new ObjectsManager(_loggerMock.Object, context, _objectSystemMock.Object, _appSettingsMock.Object,
                _ddbFactoryMock.Object, _authManagerMock.Object, new WebUtils(_authManagerMock.Object, context));

            objectManager.Invoking(item => item.List(null, MagicStrings.DefaultDatasetSlug, "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List(MagicStrings.PublicOrganizationId, null, "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List(string.Empty, MagicStrings.DefaultDatasetSlug, "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List(MagicStrings.PublicOrganizationId, string.Empty, "test")).Should().Throw<BadRequestException>();
        }

        [Test]
        public async Task List_PublicDefault_ListObjects()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var objectManager = new ObjectsManager(_loggerMock.Object, context, _objectSystemMock.Object, _appSettingsMock.Object,
                new DdbFactory(_appSettingsMock.Object), _authManagerMock.Object, new WebUtils(_authManagerMock.Object, context));

            var res = await objectManager.List(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug, null);

            res.Should().HaveCount(26);

            res = await objectManager.List(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug, "Sub");

            res.Should().HaveCount(8);

        }

        [Test]
        public async Task Get_MissingFile_NotFound()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var objectManager = new ObjectsManager(_loggerMock.Object, context, new PhysicalObjectSystem(TestStorageFolder), _appSettingsMock.Object,
                new DdbFactory(_appSettingsMock.Object), _authManagerMock.Object, new WebUtils(_authManagerMock.Object, context));

            objectManager.Invoking(async x => await x.Get(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug, "weriufbgeiughegr"))
                .Should().Throw<NotFoundException>();

        }

        [Test]
        public async Task Get_ExistingFile_FileRes()
        {
            var expectedHash = new byte[] { 152, 110, 79, 250, 177, 15, 101, 187, 24, 23, 34, 217, 117, 168, 119, 124 };

            const string expectedName = "DJI_0019.JPG";
            const ObjectType expectedObjectType = ObjectType.GeoImage;
            const string expectedContentType = "image/jpeg";
            
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var objectManager = new ObjectsManager(_loggerMock.Object, context, new PhysicalObjectSystem(TestStorageFolder), _appSettingsMock.Object,
                new DdbFactory(_appSettingsMock.Object), _authManagerMock.Object, new WebUtils(_authManagerMock.Object, context));

            var obj = await objectManager.Get(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug,
                "DJI_0019.JPG");

            obj.Name.Should().Be(expectedName);
            obj.Type.Should().Be(expectedObjectType);
            obj.ContentType.Should().Be(expectedContentType);
            MD5.Create().ComputeHash(obj.Data).Should().BeEquivalentTo(expectedHash);

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
    ""DdbStoragePath"": ""./Data/Ddb""
  }
  ");

        #endregion

        #region TestContexts

        private static RegistryContext GetTest1Context()
        {
            var options = new DbContextOptionsBuilder<RegistryContext>()
                .UseInMemoryDatabase(databaseName: "RegistryDatabase")
                .Options;

            // Insert seed data into the database using one instance of the context
            using (var context = new RegistryContext(options))
            {

                var entity = new Organization
                {
                    Id = MagicStrings.PublicOrganizationId,
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
