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
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    [TestFixture]
    public class OrganizationManagerTest
    {

        private Mock<IAuthManager> _authManagerMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Mock<IDatasetsManager> _datasetManagerMock;
        private Logger<OrganizationsManager> _organizationsManagerLogger;
        private Mock<IHttpContextAccessor> _httpContextAccessorMock;
        private Mock<IDdbManager> _ddbManagerMock;
        
        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _authManagerMock = new Mock<IAuthManager>();
            _datasetManagerMock = new Mock<IDatasetsManager>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
            _ddbManagerMock = new Mock<IDdbManager>();

            _organizationsManagerLogger = new Logger<OrganizationsManager>(LoggerFactory.Create(builder => builder.AddConsole()));

        }

        [Test]
        public async Task List_Default_Ok()
        {

            await using var context = GetTest1Context();
            await using var appContext = GetAppTest1Context();
            
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var webUtils = new WebUtils(_authManagerMock.Object, context, _appSettingsMock.Object,
                _httpContextAccessorMock.Object, _ddbManagerMock.Object);

            var organizationsManager =
                new OrganizationsManager(_authManagerMock.Object, context, webUtils, _datasetManagerMock.Object, appContext, _organizationsManagerLogger);

            var list = (await organizationsManager.List()).ToArray();

            list.Should().HaveCount(1);

            var pub = list.First();

            const string expectedDescription = "Public organization";
            const string expectedSlug = MagicStrings.PublicOrganizationSlug;
            const string expectedName = "Public";

            pub.Description.Should().Be(expectedDescription);
            pub.Slug.Should().Be(expectedSlug);
            pub.IsPublic.Should().BeTrue();
            pub.Owner.Should().BeNull();
            pub.Name.Should().Be(expectedName);

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
    ""DdbPath"": ""./ddb""}
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
        
        private static ApplicationDbContext GetAppTest1Context()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "RegistryAppDatabase-" + Guid.NewGuid())
                .Options;

            // Insert seed data into the database using one instance of the context
            using (var context = new ApplicationDbContext(options))
            {

                var adminRole = new IdentityRole
                {
                    Id = "1db5b539-6e54-4674-bb74-84732eb48204",
                    Name = "admin",
                    NormalizedName = "ADMIN",
                    ConcurrencyStamp = "72c80593-64a2-40b4-b0c4-26a9dcc06400"
                };
                
                context.Roles.Add(adminRole);

                var standardRole = new IdentityRole
                {
                    Id = "7d02507e-8eab-48c0-ba19-fea3ae644ab9",
                    Name = "standard",
                    NormalizedName = "STANDARD",
                    ConcurrencyStamp = "2e279a3c-4273-4f0a-abf6-8e97811651a9"
                };

                context.Roles.Add(standardRole);
                
                var admin = new User
                {
                    Id = "bfb579ce-8435-4c70-a365-158a3d93811f",
                    UserName = "admin",
                    Email = "admin@example.com",
                    NormalizedUserName = "ADMIN"
                };
                
                context.Users.Add(admin);

                context.UserRoles.Add(new IdentityUserRole<string>
                {
                    RoleId = "1db5b539-6e54-4674-bb74-84732eb48204",
                    UserId = "bfb579ce-8435-4c70-a365-158a3d93811f"
                });

                context.SaveChanges();
            }

            return new ApplicationDbContext(options);
        }


        #endregion
    }
}