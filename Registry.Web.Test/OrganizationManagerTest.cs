using Shouldly;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Test;

[TestFixture]
public class OrganizationManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IDatasetsManager> _datasetManagerMock;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IDdbManager> _ddbManagerMock;
    private ILogger<OrganizationsManager> _logger;
    private OrganizationsManager _organizationsManager;
    private RegistryContext _context;
    private ApplicationDbContext _appContext;
    private IUtils _webUtils;

    [SetUp]
    public async Task Setup()
    {
        // Initialize mocks
        _authManagerMock = new Mock<IAuthManager>();
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _datasetManagerMock = new Mock<IDatasetsManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _logger = CreateTestLogger<OrganizationsManager>();

        // Set up contexts
        _context = await CreateTestRegistryContext();
        _appContext = await CreateTestApplicationContext();

        // Set up default auth manager behavior
        SetupDefaultAuthManagerBehavior();

        // Initialize utils and manager
        _webUtils = new WebUtils(_authManagerMock.Object, _context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbManagerMock.Object);

        _organizationsManager = new OrganizationsManager(
            _authManagerMock.Object,
            _context,
            _webUtils,
            _datasetManagerMock.Object,
            _appContext,
            _logger);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        await _appContext.DisposeAsync();
    }

    #region Tests

    [Test]
    public async Task List_AsAdmin_ReturnsAllOrganizations()
    {
        // Arrange
        SetupAdminUser();

        // Act
        var organizations = (await _organizationsManager.List()).ToArray();

        // Assert
        organizations.Count().ShouldBe(1);
        var publicOrg = organizations.First();
        AssertPublicOrganization(publicOrg);
    }

    [Test]
    public async Task AddNew_ValidOrganization_CreatesSuccessfully()
    {
        // Arrange
        SetupAdminUser();
        var newOrg = CreateTestOrganizationDto("test-org");

        // Act
        var result = await _organizationsManager.AddNew(newOrg);

        // Assert
        result.ShouldNotBeNull();
        result.Slug.ShouldBe(newOrg.Slug);
        result.Name.ShouldBe(newOrg.Name);
        result.Description.ShouldBe(newOrg.Description);
    }

    [Test]
    public async Task Get_ExistingOrganization_ReturnsCorrectOrganization()
    {
        // Arrange
        SetupAdminUser();

        // Act
        var result = await _organizationsManager.Get(MagicStrings.PublicOrganizationSlug);

        // Assert
        result.ShouldNotBeNull();
        AssertPublicOrganization(result);
    }

    [Test]
    public async Task Edit_ValidUpdate_UpdatesSuccessfully()
    {
        // Arrange
        SetupAdminUser();
        var updateDto = new OrganizationDto
        {
            Name = "Updated Public Org",
            Description = "Updated description",
            IsPublic = true
        };

        // Act
        await _organizationsManager.Edit(MagicStrings.PublicOrganizationSlug, updateDto);
        var updated = await _organizationsManager.Get(MagicStrings.PublicOrganizationSlug);

        // Assert
        updated.Name.ShouldBe(updateDto.Name);
        updated.Description.ShouldBe(updateDto.Description);
    }

    [Test]
    public async Task Delete_ExistingOrganization_DeletesSuccessfully()
    {
        // Arrange
        SetupAdminUser();
        _datasetManagerMock.Setup(x => x.Delete(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _organizationsManager.Delete(MagicStrings.PublicOrganizationSlug);
        var organizations = await _organizationsManager.List();

        // Assert
        organizations.ShouldBeEmpty();
    }

    #region Authorization Tests

    [Test]
    public async Task List_StandardUser_ReturnsOnlyOwnedAndPublicOrganizations()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var ownedOrg = await CreateOrganizationForUser(standardUser.Id);

        // Act
        var organizations = (await _organizationsManager.List()).ToArray();

        // Assert
        organizations.Count().ShouldBe(2); // Public org + owned org
        organizations.ShouldContain(org => org.Slug == MagicStrings.PublicOrganizationSlug);
        organizations.ShouldContain(org => org.Slug == ownedOrg.Slug);
    }

    [Test]
    public async Task List_AnonymousUser_ThrowsUnauthorizedException()
    {
        // Arrange
        SetupAnonymousUser();

        // Act & Assert
        var action = () => _organizationsManager.List();
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task AddNew_StandardUser_CanOnlyCreateOwnOrganization()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var newOrg = CreateTestOrganizationDto("test-org");
        newOrg.Owner = "different-user"; // Trying to create org for different user

        // Act & Assert
        var action = () => _organizationsManager.AddNew(newOrg);
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task AddNew_AnonymousUser_ThrowsUnauthorizedException()
    {
        // Arrange
        SetupAnonymousUser();
        var newOrg = CreateTestOrganizationDto("test-org");

        // Act & Assert
        var action = () => _organizationsManager.AddNew(newOrg);
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task Edit_StandardUser_CanOnlyEditOwnOrganization()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var ownedOrg = await CreateOrganizationForUser(standardUser.Id);
        var updateDto = new OrganizationDto
        {
            Name = "Updated Org",
            Description = "Updated description",
            IsPublic = false
        };

        // Act
        await _organizationsManager.Edit(ownedOrg.Slug, updateDto);
        var updated = await _organizationsManager.Get(ownedOrg.Slug);

        // Assert
        updated.Name.ShouldBe(updateDto.Name);
        updated.Description.ShouldBe(updateDto.Description);
        updated.Owner.ShouldBe(standardUser.UserName);
    }

    /*[Test]
    public async Task Edit_StandardUser_CannotEditOthersOrganization()
    {
        // Arrange
        SetupStandardUser("standard-user");
        var updateDto = new OrganizationDto
        {
            Name = "Updated Public Org",
            Description = "Updated description",
            IsPublic = true
        };


        // Act & Assert
        var action = () => _organizationsManager.Edit(MagicStrings.PublicOrganizationSlug, updateDto);
        await Should.ThrowAsync<UnauthorizedException>(action)
            .WithMessage("Invalid user");
    }*/


    [Test]
    public async Task Delete_StandardUser_CanOnlyDeleteOwnOrganization()
    {
        // Arrange
        var standardUser = SetupStandardUser("standard-user");
        var ownedOrg = await CreateOrganizationForUser(standardUser.Id);
        _datasetManagerMock.Setup(x => x.Delete(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _organizationsManager.Delete(ownedOrg.Slug);

        // Assert
        var organizations = await _organizationsManager.List();
        organizations.ShouldNotContain(org => org.Slug == ownedOrg.Slug);
    }
/*
    [Test]
    public async Task Delete_StandardUser_CannotDeleteOthersOrganization()
    {
        // Arrange
        SetupStandardUser("standard-user");

        // Act & Assert
        var action = () => _organizationsManager.Delete(MagicStrings.PublicOrganizationSlug);
        await Should.ThrowAsync<UnauthorizedException>(action)
            .WithMessage("Invalid user");
    }*/

    #endregion

    #region Additional Helper Methods

    private User SetupStandardUser(string userId)
    {
        var standardUser = new User
        {
            Id = userId,
            UserName = userId,
            Email = "standard@example.com"
        };

        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(standardUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.UserExists(It.IsAny<string>())).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(true);

        // Only allow access to owned organizations
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync((Organization org, AccessType _) => org.OwnerId == userId || org.IsPublic);

        return standardUser;
    }

    private void SetupAnonymousUser()
    {
        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync((User)null);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(false);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync(false);
    }

    private async Task<OrganizationDto> CreateOrganizationForUser(string userId)
    {
        var org = new Organization
        {
            Slug = "test-org",
            Name = "Test Organization",
            Description = "Test Description",
            IsPublic = false,
            OwnerId = userId,
            CreationDate = DateTime.Now
        };

        await _context.Organizations.AddAsync(org);
        await _context.SaveChangesAsync();

        return org.ToDto();
    }

    #endregion

    #endregion

    #region Helper Methods

    private void SetupAdminUser()
    {
        var adminUser = new User
        {
            UserName = "admin",
            Email = "admin@example.com",
            Id = "admin-id"
        };

        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(adminUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.UserExists(It.IsAny<string>())).ReturnsAsync(true);
    }

    private void SetupDefaultAuthManagerBehavior()
    {
        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync(true);
    }

    private static OrganizationDto CreateTestOrganizationDto(string slug)
    {
        return new OrganizationDto
        {
            Slug = slug,
            Name = "Test Organization",
            Description = "Test Description",
            IsPublic = false,
            CreationDate = DateTime.Now
        };
    }

    private static void AssertPublicOrganization(OrganizationDto org)
    {
        org.Description.ShouldBe("Public organization");
        org.Slug.ShouldBe(MagicStrings.PublicOrganizationSlug);
        org.IsPublic.ShouldBeTrue();
        org.Owner.ShouldBeNull();
        org.Name.ShouldBe("Public");
    }

    private static async Task<RegistryContext> CreateTestRegistryContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .Options;

        var context = new RegistryContext(options);

        var publicOrg = new Organization
        {
            Slug = MagicStrings.PublicOrganizationSlug,
            Name = "Public",
            CreationDate = DateTime.Now,
            Description = "Public organization",
            IsPublic = true,
            OwnerId = null,
            Datasets = new List<Dataset>
            {
                new Dataset
                {
                    Slug = MagicStrings.DefaultDatasetSlug,
                    CreationDate = DateTime.Now,
                    InternalRef = Guid.Parse("0a223495-84a0-4c15-b425-c7ef88110e75")
                }
            }
        };

        await context.Organizations.AddAsync(publicOrg);
        await context.SaveChangesAsync();

        return context;
    }

    private static async Task<ApplicationDbContext> CreateTestApplicationContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryAppDatabase-{Guid.NewGuid()}")
            .Options;

        var context = new ApplicationDbContext(options);

        var adminRole = new IdentityRole
        {
            Id = "admin-role-id",
            Name = "admin",
            NormalizedName = "ADMIN"
        };

        var standardRole = new IdentityRole
        {
            Id = "standard-role-id",
            Name = "standard",
            NormalizedName = "STANDARD"
        };

        var adminUser = new User
        {
            Id = "admin-user-id",
            UserName = "admin",
            Email = "admin@example.com",
            NormalizedUserName = "ADMIN"
        };

        await context.Roles.AddRangeAsync(adminRole, standardRole);
        await context.Users.AddAsync(adminUser);
        await context.UserRoles.AddAsync(new IdentityUserRole<string>
        {
            RoleId = adminRole.Id,
            UserId = adminUser.Id
        });

        await context.SaveChangesAsync();

        return context;
    }

    #endregion
}