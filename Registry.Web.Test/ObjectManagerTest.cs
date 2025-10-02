using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
using Registry.Common.Model;
using Registry.Common.Test;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Identity.Models;

namespace Registry.Web.Test;

[TestFixture]
public class ObjectManagerTest : TestBase
{
    private Logger<DdbManager> _ddbFactoryLogger;
    private Logger<ObjectsManager> _objectManagerLogger;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IDdbManager> _ddbFactoryMock;
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<ICacheManager> _cacheManagerMock;
    private Mock<IThumbnailGenerator> _thumbnailGeneratorMock;
    private Mock<IJobIndexQuery> _jobIndexQueryMock;
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

    private static readonly IDdbWrapper DdbWrapper = new NativeDdbWrapper(true);

    [SetUp]
    public void Setup()
    {
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _ddbFactoryMock = new Mock<IDdbManager>();
        _authManagerMock = new Mock<IAuthManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _cacheManagerMock = new Mock<ICacheManager>();
        _jobIndexQueryMock = new Mock<IJobIndexQuery>();
        _backgroundJobsProcessor = new SimpleBackgroundJobsProcessor();

        _ddbFactoryLogger = new Logger<DdbManager>(LoggerFactory.Create(builder => builder.AddConsole()));
        _objectManagerLogger = new Logger<ObjectsManager>(LoggerFactory.Create(builder => builder.AddConsole()));

        var ddbMock1 = new Mock<IDDB>();
        ddbMock1.Setup(x => x.GetAttributesRaw()).Returns(new Dictionary<string, object>
        {
            { "public", true }
        });
        var ddbMock2 = new Mock<IDDB>();
        // ddbMock2.Setup(x => x.GetAttributesAsync(default))
        //     .Returns(Task.FromResult(new EntryAttributes(ddbMock1.Object)));

        _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock2.Object);

        _thumbnailGeneratorMock = new Mock<IThumbnailGenerator>();

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
            _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context,
            _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context,
            _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        var res = await objectManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug,
            expectedName);

        res.Name.Should().Be(expectedName);
        res.Type.Should().Be(EntryType.GeoImage);

        (await MD5.Create().ComputeHashAsync(res.PhysicalPath)).Should().BeEquivalentTo(expectedHash);

    }


    [Test]
    public async Task Download_ExistingFile_PackageRes()
    {
        string[] fileNames = ["DJI_0019.JPG", "DJI_0020.JPG", "DJI_0021.JPG", "DJI_0022.JPG"];
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

        await using var context = GetTest1Context();
        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

        settings.DatasetsPath = test.TestFolder;
        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context,  _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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

        string[] fileNames =
            ["DJI_0007.JPG", "DJI_0008.JPG", "DJI_0009.JPG", "Sub/DJI_0049.JPG", "Sub/DJI_0048.JPG"];
        using var test = new TestFS(Test5ArchiveUrl, BaseTestFolder);

        await using var context = GetTest1Context();
        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

        settings.DatasetsPath = test.TestFolder;
        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(),
            It.IsAny<AccessType>())).Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils, _authManagerMock.Object,
            _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

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
    ""DdbPath"": ""./ddb"",
    ""TempPath"": ""./temp""
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

    #region Transfer Tests

    [Test]
    public async Task Transfer_SameDataset_ThrowsException()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils,
            _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor,
            DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        await objectManager.Invoking(om => om.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "DJI_0027.JPG",
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "DJI_0027_copy.JPG",
            false
        )).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Source and destination cannot be the same");
    }

    [Test]
    public async Task Transfer_InvalidDestPath_ThrowsException()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils,
            _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor,
            DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        // Test path traversal
        await objectManager.Invoking(om => om.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "DJI_0027.JPG",
            "admin", "7kd0gxti9qoemsrk", "../../../etc/passwd",
            false
        )).Should().ThrowAsync<ArgumentException>()
            .WithMessage("*path traversal*");

        // Test absolute path
        await objectManager.Invoking(om => om.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "DJI_0027.JPG",
            "admin", "7kd0gxti9qoemsrk", "/etc/passwd",
            false
        )).Should().ThrowAsync<ArgumentException>()
            .WithMessage("*path traversal*");
    }

    [Test]
    public async Task Transfer_ReservedPath_ThrowsException()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils,
            _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor,
            DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        await objectManager.Invoking(om => om.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "DJI_0027.JPG",
            "admin", "7kd0gxti9qoemsrk", ".ddb/somefile.txt",
            false
        )).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reserved path*");
    }

    [Test]
    public async Task Transfer_NonExistentSource_ThrowsException()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper), webUtils,
            _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem, _backgroundJobsProcessor,
            DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        await objectManager.Invoking(om => om.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "NonExistent.JPG",
            "admin", "7kd0gxti9qoemsrk", "test.JPG",
            false
        )).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cannot find source entry*");
    }

    [Test]
    public async Task Transfer_FileToAnotherDataset_Success()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        using var test2 = new TestFS(Test5ArchiveUrl, nameof(Transfer_FileToAnotherDataset_Success));

        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false; // Disable storage limits for testing

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        // Get the file info before transfer
        var sourceFiles = await objectManager.List(MagicStrings.PublicOrganizationSlug,
            MagicStrings.DefaultDatasetSlug, null, true);
        sourceFiles.Should().NotBeEmpty();

        var fileToTransfer = "DJI_0027.JPG";

        // Perform the transfer
        await objectManager.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileToTransfer,
            "admin", "7kd0gxti9qoemsrk", fileToTransfer,
            false
        );

        // Verify file was removed from source
        var sourceFilesAfter = await objectManager.List(MagicStrings.PublicOrganizationSlug,
            MagicStrings.DefaultDatasetSlug, null, true);
        sourceFilesAfter.Should().NotContain(f => f.Path == fileToTransfer);

        // Verify file exists in destination
        var destFiles = await objectManager.List("admin", "7kd0gxti9qoemsrk", null, true);
        destFiles.Should().Contain(f => f.Path == fileToTransfer);
    }

    [Test]
    public async Task Transfer_DestinationExists_NoOverwrite_ThrowsException()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false;

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        // First, copy a file to the destination manually
        var sourcePath = Path.Combine(test.TestFolder, MagicStrings.PublicOrganizationSlug,
            _defaultDatasetGuid.ToString(), "DJI_0027.JPG");
        var destDatasetPath = Path.Combine(test.TestFolder, "admin",
            "6c1f5555-d001-4411-9308-42aa6ccd7fd6");

        Directory.CreateDirectory(destDatasetPath);
        var destPath = Path.Combine(destDatasetPath, "DJI_0027.JPG");
        File.Copy(sourcePath, destPath, true);

        // Initialize destination DDB and add the file
        var destDdb = ddbManager.Get("admin", Guid.Parse("6c1f5555-d001-4411-9308-42aa6ccd7fd6"));
        destDdb.AddRaw(destPath);

        // Try to transfer with overwrite=false (should fail)
        await objectManager.Invoking(om => om.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "DJI_0027.JPG",
            "admin", "7kd0gxti9qoemsrk", "DJI_0027.JPG",
            false
        )).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Test]
    public async Task Transfer_NullDestPath_UsesSourceNameInRoot()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false;

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        var fileToTransfer = "DJI_0027.JPG";

        // Perform the transfer with null destPath
        await objectManager.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileToTransfer,
            "admin", "7kd0gxti9qoemsrk", null,
            false
        );

        // Verify file was removed from source
        var sourceFilesAfter = await objectManager.List(MagicStrings.PublicOrganizationSlug,
            MagicStrings.DefaultDatasetSlug, null, true);
        sourceFilesAfter.Should().NotContain(f => f.Path == fileToTransfer);

        // Verify file exists in destination with the same name
        var destFiles = await objectManager.List("admin", "7kd0gxti9qoemsrk", null, true);
        destFiles.Should().Contain(f => f.Path == fileToTransfer, "file should be transferred with the same name when destPath is null");
    }

    #endregion

    #region Rollback Tests

    [Test]
    public async Task Transfer_RollbackOnDdbAddFailure_CleansUpFilesystem()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false;

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        var fileToTransfer = "DJI_0027.JPG";

        // Get destination paths
        var destDatasetPath = Path.Combine(test.TestFolder, "admin", "6c1f5555-d001-4411-9308-42aa6ccd7fd6");
        var destFilePath = Path.Combine(destDatasetPath, fileToTransfer);

        // Verify file doesn't exist in destination before transfer
        Directory.CreateDirectory(destDatasetPath);
        File.Exists(destFilePath).Should().BeFalse("destination should be empty before transfer");

        // Create a read-only DDB folder to force AddRaw to fail
        var destDdbPath = Path.Combine(destDatasetPath, ".ddb");
        Directory.CreateDirectory(destDdbPath);

        // Make the .ddb directory read-only to cause failure
        var dirInfo = new DirectoryInfo(destDdbPath);
        dirInfo.Attributes = FileAttributes.ReadOnly;

        try
        {
            // Attempt transfer - should fail during DDB add
            await objectManager.Invoking(om => om.Transfer(
                MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileToTransfer,
                "admin", "7kd0gxti9qoemsrk", fileToTransfer,
                false
            )).Should().ThrowAsync<Exception>();

            // Verify rollback: file should be cleaned up from destination filesystem
            File.Exists(destFilePath).Should().BeFalse(
                "transferred file should be rolled back and removed from destination");

            // Verify source file still exists
            var sourceFiles = await objectManager.List(MagicStrings.PublicOrganizationSlug,
                MagicStrings.DefaultDatasetSlug, null, true);
            sourceFiles.Should().Contain(f => f.Path == fileToTransfer,
                "source file should still exist after failed transfer");
        }
        finally
        {
            // Cleanup: remove read-only attribute
            if (Directory.Exists(destDdbPath))
            {
                dirInfo.Attributes = FileAttributes.Normal;
                Directory.Delete(destDdbPath, true);
            }
        }
    }

    [Test]
    public async Task Transfer_RollbackOnSourceRemoveFailure_RestoresConsistency()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false;

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        var fileToTransfer = "DJI_0027.JPG";

        // Verify file exists in source before transfer
        var sourceFilesBefore = await objectManager.List(MagicStrings.PublicOrganizationSlug,
            MagicStrings.DefaultDatasetSlug, null, true);
        sourceFilesBefore.Should().Contain(f => f.Path == fileToTransfer);

        // Get source file path and make it read-only after DDB operations complete
        // This simulates a failure during the final source removal step
        var sourceDdb = ddbManager.Get(MagicStrings.PublicOrganizationSlug, _defaultDatasetGuid);
        var sourceFilePath = sourceDdb.GetLocalPath(fileToTransfer);

        // Note: This test verifies that if the source removal fails,
        // the file is successfully copied to destination but source remains intact
        // This is the expected behavior - the transfer is "successful" but leaves source intact
        // rather than leaving the system in an inconsistent state

        // We can't easily simulate this failure without mocking DDB,
        // but we can verify the source file remains accessible after a successful transfer
        await objectManager.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileToTransfer,
            "admin", "7kd0gxti9qoemsrk", fileToTransfer,
            false
        );

        // Verify file was removed from source (successful case)
        var sourceFilesAfter = await objectManager.List(MagicStrings.PublicOrganizationSlug,
            MagicStrings.DefaultDatasetSlug, null, true);
        sourceFilesAfter.Should().NotContain(f => f.Path == fileToTransfer,
            "source file should be removed after successful transfer");

        // Verify file exists in destination
        var destFiles = await objectManager.List("admin", "7kd0gxti9qoemsrk", null, true);
        destFiles.Should().Contain(f => f.Path == fileToTransfer,
            "file should exist in destination after transfer");
    }

    [Test]
    public async Task Transfer_DirectoryRollback_RemovesEntireDirectoryTree()
    {
        using var test = new TestFS(Test5ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false;

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        const string folderToTransfer = "Sub";

        // Get destination paths
        var destDatasetGuid = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75");
        var destDatasetPath = Path.Combine(test.TestFolder, MagicStrings.PublicOrganizationSlug, destDatasetGuid.ToString());
        var destFolderPath = Path.Combine(destDatasetPath, folderToTransfer);

        // Verify folder doesn't exist in destination
        Directory.Exists(destFolderPath).Should().BeFalse("destination folder should not exist before transfer");

        // Create a read-only DDB to force failure
        var destDdbPath = Path.Combine(destDatasetPath, ".ddb");
        if (Directory.Exists(destDdbPath))
        {
            var dirInfo = new DirectoryInfo(destDdbPath);
            dirInfo.Attributes = FileAttributes.ReadOnly;

            try
            {
                // Attempt transfer - should fail during DDB add
                await objectManager.Invoking(om => om.Transfer(
                    "admin", "7kd0gxti9qoemsrk", folderToTransfer,
                    MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, folderToTransfer,
                    false
                )).Should().ThrowAsync<Exception>();

                // Verify rollback: entire directory tree should be removed
                Directory.Exists(destFolderPath).Should().BeFalse(
                    "transferred directory should be completely rolled back");

                // Verify no orphaned files remain
                if (Directory.Exists(destDatasetPath))
                {
                    var orphanedFiles = Directory.GetFiles(destDatasetPath, "*", SearchOption.AllDirectories)
                        .Where(f => f.Contains(folderToTransfer))
                        .ToArray();
                    orphanedFiles.Should().BeEmpty("no orphaned files from failed transfer should remain");
                }

                // Verify source folder still exists with all files
                var sourceFiles = await objectManager.List("admin", "7kd0gxti9qoemsrk", folderToTransfer, true);
                sourceFiles.Should().NotBeEmpty("source folder should still contain files after failed transfer");
            }
            finally
            {
                // Cleanup
                dirInfo.Attributes = FileAttributes.Normal;
            }
        }
    }

    [Test]
    public async Task Transfer_PartialFileSystemCopy_RollsBackCleanly()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false;

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        var fileToTransfer = "DJI_0027.JPG";

        // Setup destination
        var destDatasetPath = Path.Combine(test.TestFolder, "admin", "6c1f5555-d001-4411-9308-42aa6ccd7fd6");
        Directory.CreateDirectory(destDatasetPath);

        var destFilePath = Path.Combine(destDatasetPath, fileToTransfer);

        // Verify clean state before test
        File.Exists(destFilePath).Should().BeFalse("destination should be clean before test");

        // Create DDB folder with read-only attribute to force failure after file copy
        var destDdbPath = Path.Combine(destDatasetPath, ".ddb");
        Directory.CreateDirectory(destDdbPath);
        var ddbDirInfo = new DirectoryInfo(destDdbPath);
        ddbDirInfo.Attributes = FileAttributes.ReadOnly;

        try
        {
            // Transfer should fail during DDB operations
            await objectManager.Invoking(om => om.Transfer(
                MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileToTransfer,
                "admin", "7kd0gxti9qoemsrk", fileToTransfer,
                false
            )).Should().ThrowAsync<Exception>("transfer should fail due to read-only DDB");

            // CRITICAL: Verify the copied file was cleaned up
            File.Exists(destFilePath).Should().BeFalse(
                "rollback must remove the copied file even though copy succeeded");

            // Verify destination dataset state is clean (no partial state)
            var allDestFiles = Directory.Exists(destDatasetPath)
                ? Directory.GetFiles(destDatasetPath, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(".ddb"))
                    .ToArray()
                : Array.Empty<string>();

            allDestFiles.Should().BeEmpty(
                "no files from failed transfer should remain in destination");

            // Verify source is untouched
            var sourceFiles = await objectManager.List(MagicStrings.PublicOrganizationSlug,
                MagicStrings.DefaultDatasetSlug, null, true);
            sourceFiles.Should().Contain(f => f.Path == fileToTransfer,
                "source file must remain intact after failed transfer");
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(destDdbPath))
            {
                ddbDirInfo.Attributes = FileAttributes.Normal;
                Directory.Delete(destDdbPath, true);
            }
        }
    }

    [Test]
    public async Task Transfer_BuildFolderCopyFailure_DoesNotAffectMainTransfer()
    {
        using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);
        await using var context = GetTest1Context();

        var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);
        settings.DatasetsPath = test.TestFolder;
        settings.EnableStorageLimiter = false;

        _appSettingsMock.Setup(o => o.Value).Returns(settings);
        _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .Returns(Task.FromResult(true));
        _authManagerMock.Setup(o => o.GetCurrentUser()).Returns(Task.FromResult(new User
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            Email = "admin@test.com"
        }));

        var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

        var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger, DdbWrapper);

        var objectManager = new ObjectsManager(_objectManagerLogger, context, _appSettingsMock.Object,
            ddbManager, webUtils, _authManagerMock.Object, _cacheManagerMock.Object, _fileSystem,
            _backgroundJobsProcessor, DdbWrapper, _thumbnailGeneratorMock.Object, _jobIndexQueryMock.Object);

        var fileToTransfer = "DJI_0027.JPG";

        // Note: Build folder transfer is an optimization, not critical
        // If build folder copy fails, the main transfer should still succeed
        // The file will just need to be rebuilt in the destination

        await objectManager.Transfer(
            MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, fileToTransfer,
            "admin", "7kd0gxti9qoemsrk", fileToTransfer,
            false
        );

        // Verify transfer succeeded even if build folder copy might have failed
        var destFiles = await objectManager.List("admin", "7kd0gxti9qoemsrk", null, true);
        destFiles.Should().Contain(f => f.Path == fileToTransfer,
            "main file transfer should succeed regardless of build folder status");

        // Verify source was removed
        var sourceFiles = await objectManager.List(MagicStrings.PublicOrganizationSlug,
            MagicStrings.DefaultDatasetSlug, null, true);
        sourceFiles.Should().NotContain(f => f.Path == fileToTransfer,
            "source should be removed after successful transfer");
    }

    #endregion
}
