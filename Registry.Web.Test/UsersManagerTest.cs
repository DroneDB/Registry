using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Test
{
    [TestFixture]
    public class UsersManagerTest
    {

        private Mock<IAuthManager> _authManagerMock;
        private Mock<IOptions<AppSettings>> _appSettingsMock;
        private Logger<UsersManager> _usersManagerLogger;
        private Logger<OrganizationsManager> _organizationsManagerLogger;
        private Mock<SignInManager<User>> _signInManagerMock;
        private Mock<UserManager<User>> _userManagerMock;
        private Mock<RoleManager<User>> _roleManagerMock;
        private Mock<IOrganizationsManager> _organizationsManager;
        private Mock<IDatasetsManager> _datasetsManagerMock;

        [SetUp]
        public void Setup()
        {
            _appSettingsMock = new Mock<IOptions<AppSettings>>();
            _authManagerMock = new Mock<IAuthManager>();
            _signInManagerMock = new Mock<SignInManager<User>>();
            _usersManagerLogger = new Logger<UsersManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _organizationsManagerLogger = new Logger<OrganizationsManager>(LoggerFactory.Create(builder => builder.AddConsole()));
            _userManagerMock = new Mock<UserManager<User>>();
            _roleManagerMock = new Mock<RoleManager<User>>();
            _organizationsManager = new Mock<IOrganizationsManager>();
            _datasetsManagerMock = new Mock<IDatasetsManager>();
        }

        /*
         * We cannot test the UsersManager class because it's hard to create instances of SignInManager, UserManager, RoleManager
         * It is possible to mock them but it requires extra work and we are going into a deep refactor to support anonymous users so we are postponing this
         *
         */

        //[Test]
        //public async Task List_Default_Ok()
        //{
        //    await using var context = GetTest1Context();
        //    _appSettingsMock.Setup(o => o.Value).Returns(_settings);
        //    _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

        //    var utils = new WebUtils(_authManagerMock.Object, context);

        //    var organizationsManager = new OrganizationsManager(_authManagerMock.Object, context, utils, _datasetsManagerMock.Object, _organizationsManagerLogger);

        //    var usersManager = new UsersManager(_appSettingsMock.Object, _signInManagerMock.Object, _userManagerMock.Object, _roleManagerMock.Object, _authManagerMock.Object,
        //        organizationsManager, utils, _usersManagerLogger);

        //    var users = usersManager.GetAll();

        //}


        /*
         * 
         *             _userManagerMock.Setup(x => x.FindByNameAsync(userName)).Returns(Task.FromResult((User)null));
            _userManagerMock.Setup(x => x.CreateAsync(It.IsAny<User>())).Returns(Task.FromResult(IdentityResult.Success));

                     const string userName = "test";
            const string userMail = "test@test.it";
            const string userPassword = "password";

                     await usersManager.CreateUser(userName, userMail, userPassword);

         */

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