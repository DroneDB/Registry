using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Adapters;
using Registry.Web.Utilities;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Ports.DroneDB.Models;

namespace Registry.Web.Test
{
    [TestFixture]
    public class ObjectManagerTest
    {
        private Logger<DdbManager> _ddbFactoryLogger;
        private Logger<ObjectsManager> _objectManagerLogger;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IDdbManager> _ddbFactoryMock;
        private Mock<IAuthManager> _authManagerMock;
        private Mock<IHttpContextAccessor> _httpContextAccessorMock;
        private Mock<ICacheManager> _cacheManagerMock;
        private IBackgroundJobsProcessor _backgroundJobsProcessor;
        private readonly IFileSystem _fileSystem = new FileSystem();

        //private const string DataFolder = "Data";
        private const string TestStorageFolder = @"Data/Storage";
        private const string DdbTestDataFolder = @"Data/DdbTest";
        private const string StorageFolder = "Storage";
        private const string DdbFolder = "Ddb";

        private const string BaseTestFolder = "ObjectManagerTest";

        private const string Test4ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/Test4-new.zip";
        private const string Test5ArchiveUrl = "https://github.com/DroneDB/test_data/raw/master/registry/Test5-new.zip";

        private readonly Guid _defaultDatasetGuid = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75");

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _ddbFactoryMock = new Mock<IDdbManager>();
            _authManagerMock = new Mock<IAuthManager>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            _cacheManagerMock = new Mock<ICacheManager>();
            _backgroundJobsProcessor = new SimpleBackgroundJobsProcessor();

            _ddbFactoryLogger = new Logger<DdbManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _objectManagerLogger = new Logger<ObjectsManager>(LoggerFactory.Create(builder => builder.AddConsole()));

            var ddbMock1 = new Mock<IDDB>();
            ddbMock1.Setup(x => x.GetAttributesRaw()).Returns(new Dictionary<string, object>
            {
                { "public", true }
            });
            var ddbMock2 = new Mock<IDDB>();
            ddbMock2.Setup(x => x.GetAttributesAsync(default))
                .Returns(Task.FromResult(new EntryAttributes(ddbMock1.Object)));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock2.Object);
        }

        [Test]
        public async Task List_NullParameters_BadRequestException()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(JsonConvert.DeserializeObject<AppSettings>(_settingsJson));
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
                _ddbFactoryMock.Object, webUtils, _authManagerMock.Object, _cacheManagerMock.Object,
                _fileSystem, _backgroundJobsProcessor);

            await objectManager.Invoking(item => item.List(null, MagicStrings.DefaultDatasetSlug, "test"))
                .Should().ThrowAsync<BadRequestException>();

            await objectManager.Invoking(item => item.List(MagicStrings.PublicOrganizationSlug, null, "test"))
                .Should().ThrowAsync<BadRequestException>();

            await objectManager.Invoking(item => item.List(string.Empty, MagicStrings.DefaultDatasetSlug, "test"))
                .Should().ThrowAsync<BadRequestException>();

            await objectManager.Invoking(item => item.List(MagicStrings.PublicOrganizationSlug, string.Empty, "test"))
                .Should().ThrowAsync<BadRequestException>();
        }

        [Test]
        public async Task List_PublicDefault_ListObjects()
        {
            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
            await using var context = GetTest1Context();

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context,
                _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            var res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                null, true);

            res.Should().HaveCount(24);

            res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "Pub");

            // We dont consider the naked folder 'Pub'
            res.Should().HaveCount(4);
        }

        [Test]
        public async Task Search_PublicDefault_ListObjects()
        {
            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
            await using var context = GetTest1Context();

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context,
                _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            var res = await objectManager.Search(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "DJI*");

            res.Should().HaveCount(18);

            res = await objectManager.Search(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "*003*");
            res.Should().HaveCount(6);

            res = await objectManager.Search(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "*0033*");
            res.Should().HaveCount(1);

            res = await objectManager.Search(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "*217*", "Pub");
            res.Should().HaveCount(1);

            res = await objectManager.Search(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "*201*", "Pub", false);
            res.Should().HaveCount(1);
        }

        [Test]
        public async Task Get_MissingFile_NotFound()
        {
            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
            await using var context = GetTest1Context();

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            await objectManager.Invoking(async x => await x.Get(MagicStrings.PublicOrganizationSlug,
                    MagicStrings.DefaultDatasetSlug, "weriufbgeiughegr"))
                .Should().ThrowAsync<NotFoundException>();
        }

        [Test]
        public async Task Get_ExistingFile_FileRes()
        {
            var expectedHash = new byte[] { 152, 110, 79, 250, 177, 15, 101, 187, 24, 23, 34, 217, 117, 168, 119, 124 };

            const string expectedName = "DJI_0019.JPG";
            const EntryType expectedObjectType = EntryType.GeoImage;
            const string expectedContentType = "image/jpeg";

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

            await using var context = GetTest1Context();
            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            var obj = await objectManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "DJI_0019.JPG");

            obj.Name.Should().Be(expectedName);
            obj.Type.Should().Be(expectedObjectType);
            obj.ContentType.Should().Be(expectedContentType);
            
            (await MD5.Create().ComputeHashAsync(obj.PhysicalPath)).Should().BeEquivalentTo(expectedHash);
        }

        [Test]
        public async Task Download_ExistingFile_FileRes()
        {
            var expectedHash = new byte[] { 152, 110, 79, 250, 177, 15, 101, 187, 24, 23, 34, 217, 117, 168, 119, 124 };

            const string expectedName = "DJI_0019.JPG";
            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

            await using var context = GetTest1Context();
            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            var res = await objectManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                expectedName);

            res.Name.Should().Be(expectedName);
            res.Type.Should().Be(EntryType.GeoImage);

            (await MD5.Create().ComputeHashAsync(res.PhysicalPath)).Should().BeEquivalentTo(expectedHash);
           
        }


        [Test]
        public async Task Download_ExistingFile_PackageRes()
        {
            string[] fileNames = { "DJI_0019.JPG", "DJI_0020.JPG", "DJI_0021.JPG", "DJI_0022.JPG" };
            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

            await using var context = GetTest1Context();
            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context,  _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            var res = await objectManager.DownloadStream(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileNames);

            res.Name.Should().EndWith(".zip");

            await using var memoryStream = new MemoryStream();
            await res.CopyToAsync(memoryStream);
            memoryStream.Reset();

            var md5 = MD5.Create();

            // Let's check if the archive is not corrupted and all the files have the right checksums
            using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                Debug.WriteLine(entry.FullName);
                var obj = await objectManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                    entry.FullName);

                // We could use entry.Crc32 but md5 comes so handy
                await using var stream = entry.Open();
                var expectedHash = await md5.ComputeHashAsync(obj.PhysicalPath);
                var hash = await md5.ComputeHashAsync(stream);

                hash.Should().BeEquivalentTo(expectedHash);
            }
        }


        [Test]
        public async Task Download_ExistingFileInSubfolders_PackageRes()
        {
            const string organizationSlug = "admin";
            const string datasetSlug = "7kd0gxti9qoemsrk";
            Guid adminDatasetGuid = Guid.Parse("6c1f5555-d001-4411-9308-42aa6ccd7fd6");

            string[] fileNames =
                { "DJI_0007.JPG", "DJI_0008.JPG", "DJI_0009.JPG", "Sub/DJI_0049.JPG", "Sub/DJI_0048.JPG" };
            using var test = new TestFS(Test5ArchiveUrl, BaseTestFolder);

            await using var context = GetTest1Context();
            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            var res = await objectManager.DownloadStream(organizationSlug, datasetSlug,
                fileNames);

            res.Name.Should().EndWith(".zip");

            var md5 = MD5.Create();
            
            await using var memoryStream = new MemoryStream();
            await res.CopyToAsync(memoryStream);
            memoryStream.Reset();

            // Let's check if the archive is not corrupted and all the files have the right checksums
            using var archive = new ZipArchive(memoryStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                Debug.WriteLine(entry.FullName);
                var obj = await objectManager.Get(organizationSlug, datasetSlug,
                    entry.FullName);

                // We could use entry.Crc32 but md5 comes so handy
                await using var stream = entry.Open();
                var expectedHash = await md5.ComputeHashAsync(obj.PhysicalPath);
                var hash = await md5.ComputeHashAsync(stream);

                hash.Should().BeEquivalentTo(expectedHash);
            }
        }


        [Test]
        public async Task AddNew_File_FileRes()
        {
            const string fileName = "DJI_0028.JPG";

            await using var context = GetTest1Context();
            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            var res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName);

            res.Should().HaveCount(1);

            await objectManager.Delete(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileName);

            res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName);

            res.Should().HaveCount(0);

            var newFileUrl =
                "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" +
                fileName;

            var ret = await objectManager.AddNew(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName, CommonUtils.SmartDownloadData(newFileUrl));

            res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                fileName);

            res.Should().HaveCount(1);
        }

        [Test]
        public async Task EndToEnd_HappyPath()
        {
            
             // 1) List files
             // 3) Add folder 'Test'
             // 4) Add file 'DJI_0021.JPG' inside folder 'Test'
             // 5) Move file 'DJI_0020.JPG' inside folder 'Test'
             // 6) Move folder 'Test' to 'Test1'
             // 7) Delete file 'Test/DJI_0021.JPG'
             // 8) Delete folder 'Test1'
             

            const string fileName = "DJI_0028.JPG";
            const string fileName2 = "DJI_0020.JPG";

            await using var context = GetTest1Context();
            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = test.TestFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
                new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger), webUtils, _authManagerMock.Object,
                _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor);

            (await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).Should()
                .HaveCount(20);

            var newres = await objectManager.AddNew(MagicStrings.PublicOrganizationSlug,
                MagicStrings.DefaultDatasetSlug, "Test");
            newres.Size.Should().Be(0);
            //newres.ContentType.Should().BeNull();
            newres.Path.Should().Be("Test");

            const string newFileUrl =
                "https://github.com/DroneDB/test_data/raw/master/test-datasets/drone_dataset_brighton_beach/" +
                fileName;

            await objectManager.AddNew(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                "Test/" + fileName, CommonUtils.SmartDownloadData(newFileUrl));

            (await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                    "Test/" + fileName))
                .Should().HaveCount(1);

            await objectManager.Move(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileName2,
                "Test/" + fileName2);

            (await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
                    "Test/" + fileName2))
                .Should().HaveCount(1);

            await objectManager.Move(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "Test",
                "Test2");

            (await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "Test2"))
                .Should().HaveCount(2);

            //await objectManager.Delete(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileName);

            //res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
            //    fileName);

            //res.Should().HaveCount(0);


            //res = await objectManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
            //    fileName);

            //res.Should().HaveCount(1);
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
    ""DdbPath"": ""./ddb""
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

                entity = new Organization
                {
                    Slug = "admin",
                    Name = "admin",
                    CreationDate = DateTime.Now,
                    Description = "Admin",
                    IsPublic = true,
                    OwnerId = null
                };
                ds = new Dataset
                {
                    Slug = "7kd0gxti9qoemsrk",
                    //Name = "7kd0gxti9qoemsrk",
                    //IsPublic = true,
                    CreationDate = DateTime.Now,
                    //LastUpdate = DateTime.Now,
                    InternalRef = Guid.Parse("6c1f5555-d001-4411-9308-42aa6ccd7fd6")
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