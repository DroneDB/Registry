using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Test
{
    [TestFixture]
    public class WebUtilsTest : TestBase
    {

        private Mock<IAuthManager> _authManagerMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IHttpContextAccessor> _httpContextAccessorMock;
        private Mock<IDdbManager> _ddbManagerMock;

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _authManagerMock = new Mock<IAuthManager>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            _ddbManagerMock = new Mock<IDdbManager>();
        }


        [Test]
        public async Task GetFreeOrganizationSlug_SimpleCase()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbManagerMock.Object);

            const string organizationName = "uav4geo";
            const string expectedOrganizationSlug = "uav4geo";

            var slug = webUtils.GetFreeOrganizationSlug(organizationName);

            slug.Should().Be(expectedOrganizationSlug);

        }


        [Test]
        public async Task GetFreeOrganizationSlug_OverlappingCase()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbManagerMock.Object);

            const string organizationName = "public";
            const string expectedOrganizationSlug = "public-1";

            var slug = webUtils.GetFreeOrganizationSlug(organizationName);

            slug.Should().Be(expectedOrganizationSlug);

        }

        [Test]
        public async Task GetFreeOrganizationSlug_DoubleOverlappingCase()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            const string organizationName = "public";
            const string expectedOrganizationSlug = "public-2";

            context.Add(new Organization
            {
                Slug = MagicStrings.PublicOrganizationSlug + "-1",
                Name = organizationName + "-1",
                CreationDate = DateTime.Now,
                Description = organizationName + "-1 organization",
                IsPublic = true,
                OwnerId = null
            });
            await context.SaveChangesAsync();

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbManagerMock.Object);

            var slug = webUtils.GetFreeOrganizationSlug(organizationName);

            slug.Should().Be(expectedOrganizationSlug);

        }

        [Test]
        public async Task GetFreeOrganizationSlug_TripleOverlappingCase()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            const string organizationName = "public";
            const string expectedOrganizationSlug = "public-3";

            context.Add(new Organization
            {
                Slug = MagicStrings.PublicOrganizationSlug + "-1",
                Name = organizationName + "-1",
                CreationDate = DateTime.Now,
                Description = organizationName + "-1 organization",
                IsPublic = true,
                OwnerId = null
            });

            context.Add(new Organization
            {
                Slug = MagicStrings.PublicOrganizationSlug + "-2",
                Name = organizationName + "-2",
                CreationDate = DateTime.Now,
                Description = organizationName + "-2 organization",
                IsPublic = true,
                OwnerId = null
            });

            await context.SaveChangesAsync();

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbManagerMock.Object);


            var slug = webUtils.GetFreeOrganizationSlug(organizationName);

            slug.Should().Be(expectedOrganizationSlug);

        }

        [Test]
        public void ToSlug_EmptyString_Exception()
        {
            var str = string.Empty;

            str.Invoking(s => s.ToSlug()).Should().Throw<ArgumentException>();
        }

        [Test]
        public void ToSlug_SimpleString_ValidSlug()
        {
            const string str = "òàùè";

            var slug = str.ToSlug();

            slug.Should().Be("oaue");

            slug.IsValidSlug().Should().BeTrue();
        }

        [Test]
        public void ToSlug_ComplexString_ValidSlug()
        {
            const string str = ":;:ç°§ç§é*{1._↓-&%$/&%$)=(/\n\ta";

            var slug = str.ToSlug();

            slug.Should().Be("0---c--c-e--1-_---------------a");
            
            slug.IsValidSlug().Should().BeTrue();

        }

        [Test]
        public void ToSlug_ComplexString2_ValidSlug()
        {
            const string str = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789éèàù°§ç§:;,.-_$&%()=+*{}[]|@#~?/*§!\"'<>";

            var slug = str.ToSlug();

            slug.Should()
                .Be(
                    "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz0123456789eeau--c------_-------------------------");
           
            slug.IsValidSlug().Should().BeTrue();

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