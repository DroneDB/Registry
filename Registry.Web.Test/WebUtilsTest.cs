﻿using System;
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
using Registry.Web.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Test
{
    [TestFixture]
    public class WebUtilsTest
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


        [Test]
        public async Task GetFreeOrganizationSlug_SimpleCase()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var utils = new WebUtils(_authManagerMock.Object, context);

            const string organizationName = "uav4geo";
            const string expectedOrganizationSlug = "uav4geo";

            var slug = utils.GetFreeOrganizationSlug(organizationName);

            slug.Should().Be(expectedOrganizationSlug);

        }


        [Test]
        public async Task GetFreeOrganizationSlug_OverlappingCase()
        {
            await using var context = GetTest1Context();
            _appSettingsMock.Setup(o => o.Value).Returns(_settings);
            _authManagerMock.Setup(o => o.IsUserAdmin()).Returns(Task.FromResult(true));

            var utils = new WebUtils(_authManagerMock.Object, context);

            const string organizationName = "public";
            const string expectedOrganizationSlug = "public-1";

            var slug = utils.GetFreeOrganizationSlug(organizationName);

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

            var utils = new WebUtils(_authManagerMock.Object, context);

            var slug = utils.GetFreeOrganizationSlug(organizationName);

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

            var utils = new WebUtils(_authManagerMock.Object, context);


            var slug = utils.GetFreeOrganizationSlug(organizationName);

            slug.Should().Be(expectedOrganizationSlug);

        }

        [Test]
        public void ToSlug_EmptyString_Exception()
        {
            var str = string.Empty;

            str.Invoking(s => s.ToSlug()).Should().Throw<ArgumentException>();
        }

        [Test]
        public void ToSlug_SimpleString_Exception()
        {
            const string str = "òàùè";

            var slug = str.ToSlug();

            slug.Should().Be("oaue");
        }

        [Test]
        public void ToSlug_ComplexString_Exception()
        {
            const string str = ":;:ç°§ç§é*{1↓-&%$/&%$)=(/\n\ta";

            var slug = str.ToSlug();

            slug.Should().Be("0---c--c-e--1---------------a");
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
    ""DdbPath"": ""./ddb"",
""SupportedDdbVersion"": {
      ""Major"": 0,
      ""Minor"": 9,
      ""Build"": 3
    }
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