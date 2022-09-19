using System;
using System.Collections.Generic;
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
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Entry = Registry.Ports.DroneDB.Models.Entry;

namespace Registry.Web.Test
{
    [TestFixture]
    public class DatasetManagerTest
    {

        private Mock<IAuthManager> _authManagerMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IDatasetsManager> _datasetManagerMock;
        private Logger<DatasetsManager> _datasetsManagerLogger;
        private Mock<IDdbManager> _ddbFactoryMock;
        private Mock<IObjectsManager> _objectsManagerMock;
        private Mock<IHttpContextAccessor> _httpContextAccessorMock;
        private Mock<IStacManager> _stacManagerMock;

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _authManagerMock = new Mock<IAuthManager>();
            _datasetManagerMock = new Mock<IDatasetsManager>();
            _ddbFactoryMock = new Mock<IDdbManager>();
            _objectsManagerMock = new Mock<IObjectsManager>();
            _stacManagerMock = new Mock<IStacManager>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            _datasetsManagerLogger = new Logger<DatasetsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
        }

        [Test]
        public async Task List_Default_Ok()
        {
            const string expectedSlug = MagicStrings.DefaultDatasetSlug;
            const string expectedName = "Default";


            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Dataset>(), 
                It.IsAny<AccessType>())).Returns(Task.FromResult(true));
            _authManagerMock.Setup(o => o.RequestAccess(It.IsAny<Organization>(), 
                It.IsAny<AccessType>())).Returns(Task.FromResult(true));
           
            var utils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object, _httpContextAccessorMock.Object, _ddbFactoryMock.Object);

            var ddbMock = new Mock<IDDB>();
            ddbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry{
                    Properties = new Dictionary<string, object>
                    {
                        {"public", true },
                        {"name", expectedName}
                    },
                    Size = 1000,
                    ModifiedTime = DateTime.Now
                }
            ));

            _ddbFactoryMock.Setup(x => x.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddbMock.Object);

            var datasetsManager = new DatasetsManager(context, utils, _datasetsManagerLogger,
                _objectsManagerMock.Object, _stacManagerMock.Object, _ddbFactoryMock.Object, _authManagerMock.Object);

            var list = (await datasetsManager.List(MagicStrings.PublicOrganizationSlug)).ToArray();

            list.Should().HaveCount(1);

            var pub = list.First();

            pub.Slug.Should().Be(expectedSlug);
            
            // TODO: Check test data: this should be true
            pub.Properties["public"].Should().Be(true);
            pub.Properties["name"].Should().Be(expectedName);


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
    ""DdbPath"": ""./ddb""
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

        #endregion
    }
}