using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    [TestFixture]
    class MetaManagerTest
    {
        private Logger<DdbManager> _ddbFactoryLogger;
        private Logger<MetaManager> _metaManagerLogger;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IAuthManager> _authManagerMock;
        private Mock<IHttpContextAccessor> _httpContextAccessorMock;

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
            _authManagerMock = new Mock<IAuthManager>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();

            _ddbFactoryLogger = new Logger<DdbManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _metaManagerLogger = new Logger<MetaManager>(LoggerFactory.Create(builder => builder.AddConsole()));
        }

        [Test]
        public async Task List_Empty_NoMeta()
        {
            await using var context = GetTest1Context();

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = Path.Combine(test.TestFolder, DdbFolder);
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            
            var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, ddbManager);

            var metaManager = new MetaManager(_metaManagerLogger, ddbManager, _authManagerMock.Object,  webUtils);

            var res = await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug);

            res.Should().BeEmpty();
        }

        [Test]
        public async Task AddListRemove_HappyPath_Ok()
        {
            await using var context = GetTest1Context();

            using var test = new TestFS(Test4ArchiveUrl, BaseTestFolder);

            var settings = JsonConvert.DeserializeObject<AppSettings>(_settingsJson);

            settings.DatasetsPath = Path.Combine(test.TestFolder, DdbFolder);
            _appSettingsMock.Setup(o => o.Value).Returns(settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.IsOwnerOrAdmin(It.IsAny<Dataset>())).Returns(Task.FromResult(true));

            var ddbManager = new DdbManager(_appSettingsMock.Object, _ddbFactoryLogger);

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, ddbManager);

            var metaManager = new MetaManager(_metaManagerLogger, ddbManager, _authManagerMock.Object, webUtils);

            var a = await metaManager.Add(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, 
                "annotations", "{\"test\":123}");

            a.Data["test"].ToObject<int>().Should().Be(123);

            var res = await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug);

            res.Should().HaveCount(1);
            res.First().Count.Should().Be(1);
            res.First().Key.Should().Be("annotations");

            var a2 = await metaManager.Add(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "annotations",
                "{\"test\":4124,\"pippo\":\"ciao\"}");

            a2.Data["test"].ToObject<int>().Should().Be(4124);
            a2.Data["pippo"].ToObject<string>().Should().Be("ciao");

            (await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).Should().HaveCount(1);

            (await metaManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "annotations"))
                .Should().HaveCount(2);

            (await metaManager.Remove(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, a.Id)).Should().Be(1);

            (await metaManager.Get(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "annotations"))
                .Should().HaveCount(1);

            (await metaManager.Unset(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug, "annotations")).Should().Be(1);

            (await metaManager.List(MagicStrings.PublicOrganizationSlug, MagicStrings.DefaultDatasetSlug)).Should()
                .BeEmpty();

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

