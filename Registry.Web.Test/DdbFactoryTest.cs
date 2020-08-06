using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Web.Models;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Test
{
    [TestFixture]
    public class DdbFactoryTest
    {
        private Mock<IOptions<AppSettings>> _appSettingsMock;

        private const string TestDataFolder = @"Data/Ddb";

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _settings.DdbStoragePath = TestDataFolder;
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);

        }

        [Test]
        public void Ctor_ExistingDatabase_Ok()
        {

            var factory = new DdbFactory(_appSettingsMock.Object);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);
            
        }

        [Test]
        public void Ctor_MissingDatabase_IOException()
        {
            var factory = new DdbFactory(_appSettingsMock.Object);

            factory.Invoking(x => x.GetDdb("vlwefwef", MagicStrings.DefaultDatasetSlug))
                .Should().Throw<IOException>();

        }

        [Test]
        public void Search_MissingEntry_Empty()
        {
            var factory = new DdbFactory(_appSettingsMock.Object);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);

            var res = ddb.Search("asasdadas.jpg");

            res.Should().BeEmpty();

        }

        [Test]
        public void Search_ExistingEntry_Entry()
        {
            var factory = new DdbFactory(_appSettingsMock.Object);

            var ddb = factory.GetDdb(MagicStrings.PublicOrganizationId, MagicStrings.DefaultDatasetSlug);
            
            var res = ddb.Search("20200610_144436.jpg");

            res.Should().HaveCount(1);

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
    },
    ""DdbStoragePath"": ""./ddbstore""
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

    }
}
