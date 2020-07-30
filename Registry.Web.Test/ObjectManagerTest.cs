using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
using Registry.Ports.ObjectSystem;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    public class ObjectManagerTest
    {
        private Mock<ILogger<ObjectsManager>> _loggerMock;
        private Mock<IObjectSystem> _objectSystemMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        
        private IUtils _utils = new WebUtils();

        const string PublicOrganizationId = "public";
        const string DefaultDatasetId = "default";

        [SetUp]
        public void Setup()
        {
            _loggerMock = new Mock<ILogger<ObjectsManager>>();
            _objectSystemMock = new Mock<IObjectSystem>();
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
        }

        [Test]
        public void List_NullParameters_BadRequestException()
        {
            using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            var objectManager = new ObjectsManager(_loggerMock.Object, context, _objectSystemMock.Object, _appSettingsMock.Object);

            objectManager.Invoking(item => item.List(null, "test", "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List("test", null, "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List(string.Empty, "test", "test")).Should().Throw<BadRequestException>();
            objectManager.Invoking(item => item.List("test", string.Empty, "test")).Should().Throw<BadRequestException>();
        }

        [Ignore("Not implemented yet")]
        [Test]
        public void List_Uninitialized_NotFoundException()
        {

            using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

            _objectSystemMock.Setup(item => item.BucketExistsAsync(It.IsAny<string>(), default))
                .Returns(Task.FromResult(false));

            var objectManager = new ObjectsManager(_loggerMock.Object, context, _objectSystemMock.Object, _appSettingsMock.Object);

            objectManager.Invoking(o => o.List(PublicOrganizationId, DefaultDatasetId, null)).Should()
                .Throw<NotFoundException>();

        }

        #region Test Data

        private readonly AppSettings _settings = JsonConvert.DeserializeObject<AppSettings>(@"{
  ""AppSettings"": {
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
    }
  },
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information"",
      ""Microsoft"": ""Warning"",
      ""Microsoft.Hosting.Lifetime"": ""Information""
    }
  },
  ""AllowedHosts"": ""*"",
  ""ConnectionStrings"": {
    ""IdentityConnection"": ""Data Source=App_Data/identity.db;Mode=ReadWriteCreate"",
    ""RegistryConnection"": ""Data Source=App_Data/registry.db;Mode=ReadWriteCreate""
  },
  ""DefaultAdmin"": null
}");

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
