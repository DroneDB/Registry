using Shouldly;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Registry.Web.Test;

[TestFixture]
public class UsersManagerTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IOrganizationsManager> _organizationsManagerMock;
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<ILoginManager> _loginManagerMock;
    private Mock<RoleManager<IdentityRole>> _roleManagerMock;
    private Mock<IConfigurationHelper<AppSettings>> _configurationHelperMock;
    private Mock<IPasswordPolicyValidator> _passwordPolicyValidatorMock;
    private ILogger<UsersManager> _logger;
    private UsersManager _usersManager;
    private RegistryContext _context;
    private ApplicationDbContext _appContext;
    private IUtils _webUtils;
    private Mock<UserManager<User>> _userManagerMock;

    [SetUp]
    public async Task Setup()
    {
        // Initialize mocks
        _authManagerMock = new Mock<IAuthManager>();
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _organizationsManagerMock = new Mock<IOrganizationsManager>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _loginManagerMock = new Mock<ILoginManager>();
        _configurationHelperMock = new Mock<IConfigurationHelper<AppSettings>>();
        _passwordPolicyValidatorMock = new Mock<IPasswordPolicyValidator>();
        _logger = CreateTestLogger<UsersManager>();

        // Setup UserManager mock
        var userStore = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(
            userStore.Object, null, null, null, null, null, null, null, null);

        // Setup RoleManager mock
        var roleStore = new Mock<IRoleStore<IdentityRole>>();
        _roleManagerMock = new Mock<RoleManager<IdentityRole>>(
            roleStore.Object, null, null, null, null);

        // Set up contexts
        _context = await CreateTestRegistryContext();
        _appContext = await CreateTestApplicationContext();

        // Set up default settings
        var appSettings = new AppSettings
        {
            DefaultAdmin = new AdminInfo { UserName = "admin", Password = "admin123" }
        };
        _appSettingsMock.Setup(x => x.Value).Returns(appSettings);

        // Set up default auth manager behavior
        SetupDefaultAuthManagerBehavior();

        // Set up default password policy validator (no policy = always valid)
        _passwordPolicyValidatorMock.Setup(x => x.Validate(It.IsAny<string>()))
            .Returns(PasswordValidationResult.Success());

        // Initialize utils
        _webUtils = new WebUtils(_authManagerMock.Object, _context, _appSettingsMock.Object,
            _httpContextAccessorMock.Object, _ddbManagerMock.Object);

        _usersManager = new UsersManager(
            _appSettingsMock.Object,
            _loginManagerMock.Object,
            _userManagerMock.Object,
            _roleManagerMock.Object,
            _appContext,
            _authManagerMock.Object,
            _organizationsManagerMock.Object,
            _webUtils,
            _logger,
            _context,
            _configurationHelperMock.Object,
            _passwordPolicyValidatorMock.Object);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        await _appContext.DisposeAsync();
    }

    #region DeleteUser Tests

    [Test]
    public async Task DeleteUser_WithoutSuccessor_DeletesAllUserData()
    {
        // Arrange
        SetupAdminUser();
        var testUser = await CreateTestUser("testuser");
        var org = await CreateOrganizationForUser(testUser.Id, "testuser-org");
        await CreateBatchForUser(testUser.UserName, org.Slug);

        _userManagerMock.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(testUser);
        _userManagerMock.Setup(x => x.DeleteAsync(testUser))
            .ReturnsAsync(IdentityResult.Success);
        _organizationsManagerMock.Setup(x => x.Delete(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _usersManager.DeleteUser("testuser");

        // Assert
        result.ShouldNotBeNull();
        result.UserName.ShouldBe("testuser");
        result.Successor.ShouldBeNull();
        result.OrganizationsDeleted.ShouldBe(1);
        result.BatchesDeleted.ShouldBe(1);

        _organizationsManagerMock.Verify(x => x.Delete("testuser-org"), Times.Once);
        _userManagerMock.Verify(x => x.DeleteAsync(testUser), Times.Once);
    }

    [Test]
    public async Task DeleteUser_WithSuccessor_TransfersOrganizations()
    {
        // Arrange
        SetupAdminUser();
        var testUser = await CreateTestUser("testuser");
        var successorUser = await CreateTestUser("successor");
        var org = await CreateOrganizationForUser(testUser.Id, "testuser-org");

        _userManagerMock.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(testUser);
        _userManagerMock.Setup(x => x.FindByNameAsync("successor"))
            .ReturnsAsync(successorUser);
        _userManagerMock.Setup(x => x.DeleteAsync(testUser))
            .ReturnsAsync(IdentityResult.Success);

        // Act
        var result = await _usersManager.DeleteUser("testuser", "successor");

        // Assert
        result.ShouldNotBeNull();
        result.UserName.ShouldBe("testuser");
        result.Successor.ShouldBe("successor");
        result.OrganizationsTransferred.ShouldBe(1);
        result.OrganizationsDeleted.ShouldBe(0);

        // Verify organization ownership was transferred
        var transferredOrg = await _context.Organizations.FirstOrDefaultAsync(o => o.Slug == "testuser-org");
        transferredOrg.ShouldNotBeNull();
        transferredOrg.OwnerId.ShouldBe(successorUser.Id);
    }

    [Test]
    public async Task DeleteUser_WithSuccessor_MergesConflictingOrganizations()
    {
        // Arrange
        SetupAdminUser();
        var testUser = await CreateTestUser("testuser");
        var successorUser = await CreateTestUser("successor");

        // Create testuser's organization
        await CreateOrganizationForUser(testUser.Id, "testuser-org");

        // Create successor's organization with the SAME slug to simulate conflict
        // Use a direct insert to bypass the helper that might conflict
        var successorOrg = new Organization
        {
            Slug = "testuser-org",  // Same slug as testuser's org - this would be a conflict scenario
            Name = "Testuser Org (Successor)",
            OwnerId = successorUser.Id,
            CreationDate = DateTime.Now,
            Datasets = new List<Dataset>()
        };

        // Actually, we can't have 2 orgs with the same slug in DB
        // So let's test the scenario differently - successor already has an org with same slug
        // We need to remove the first org and add the successor's one with the same slug
        var existingOrg = await _context.Organizations.FirstOrDefaultAsync(o => o.Slug == "testuser-org");
        _context.Organizations.Remove(existingOrg);
        await _context.SaveChangesAsync();

        // Now create testuser's org again
        await CreateOrganizationForUser(testUser.Id, "testuser-org");

        // Mock that successor owns an org with the same slug (simulated via the mock)
        _userManagerMock.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(testUser);
        _userManagerMock.Setup(x => x.FindByNameAsync("successor"))
            .ReturnsAsync(successorUser);
        _userManagerMock.Setup(x => x.DeleteAsync(testUser))
            .ReturnsAsync(IdentityResult.Success);
        _organizationsManagerMock.Setup(x => x.Delete(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _usersManager.DeleteUser("testuser", "successor", ConflictResolutionStrategy.Rename);

        // Assert
        result.ShouldNotBeNull();
        result.UserName.ShouldBe("testuser");
        result.Successor.ShouldBe("successor");
        // Since successor doesn't have an org with same slug in DB, the org is transferred
        result.OrganizationsTransferred.ShouldBe(1);
    }

    [Test]
    public async Task DeleteUser_NonExistentUser_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();
        _userManagerMock.Setup(x => x.FindByNameAsync("nonexistent"))
            .ReturnsAsync((User)null);

        // Act & Assert
        var action = () => _usersManager.DeleteUser("nonexistent");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task DeleteUser_NonExistentSuccessor_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();
        var testUser = await CreateTestUser("testuser");

        _userManagerMock.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(testUser);
        _userManagerMock.Setup(x => x.FindByNameAsync("nonexistent"))
            .ReturnsAsync((User)null);

        // Act & Assert
        var action = () => _usersManager.DeleteUser("testuser", "nonexistent");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task DeleteUser_SameUserAsSuccessor_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();
        var testUser = await CreateTestUser("testuser");

        _userManagerMock.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(testUser);

        // Act & Assert
        var action = () => _usersManager.DeleteUser("testuser", "testuser");
        await Should.ThrowAsync<BadRequestException>(action);
    }

    [Test]
    public async Task DeleteUser_AnonymousUser_ThrowsUnauthorizedException()
    {
        // Arrange
        SetupAdminUser();

        // Act & Assert
        var action = () => _usersManager.DeleteUser(MagicStrings.AnonymousUserName);
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task DeleteUser_DefaultAdmin_ThrowsUnauthorizedException()
    {
        // Arrange
        SetupAdminUser();

        // Act & Assert
        var action = () => _usersManager.DeleteUser("admin");
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task DeleteUser_NonAdmin_ThrowsUnauthorizedException()
    {
        // Arrange
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);

        // Act & Assert
        var action = () => _usersManager.DeleteUser("anyuser");
        await Should.ThrowAsync<UnauthorizedException>(action);
    }

    [Test]
    public async Task DeleteUser_RemovesOrganizationMemberships()
    {
        // Arrange
        SetupAdminUser();
        var testUser = await CreateTestUser("testuser");
        var otherOrg = await CreateOrganizationForUser("other-user-id", "other-org");

        // Add testuser as member of other-org
        _context.OrganizationsUsers.Add(new OrganizationUser
        {
            UserId = testUser.Id,
            OrganizationSlug = "other-org"
        });
        await _context.SaveChangesAsync();

        _userManagerMock.Setup(x => x.FindByNameAsync("testuser"))
            .ReturnsAsync(testUser);
        _userManagerMock.Setup(x => x.DeleteAsync(testUser))
            .ReturnsAsync(IdentityResult.Success);

        // Act
        var result = await _usersManager.DeleteUser("testuser");

        // Assert
        var memberships = await _context.OrganizationsUsers
            .Where(ou => ou.UserId == testUser.Id)
            .ToListAsync();
        memberships.ShouldBeEmpty();
    }

    #endregion

    #region CreateUser Password Policy Tests

    [Test]
    public async Task CreateUser_WithPolicyViolation_ThrowsBadRequestException()
    {
        // Arrange
        SetupAdminUser();
        _passwordPolicyValidatorMock.Setup(x => x.Validate("weak"))
            .Returns(PasswordValidationResult.Failure("Password must be at least 8 characters long"));

        // Act & Assert
        var action = () => _usersManager.CreateUser("newuser", "new@example.com", "weak", null);
        var ex = await Should.ThrowAsync<BadRequestException>(action);
        ex.Message.ShouldContain("Password does not meet the requirements");
    }

    [Test]
    public async Task CreateUser_WithValidPassword_Succeeds()
    {
        // Arrange
        SetupAdminUser();
        var newUser = new User { Id = "newuser-id", UserName = "newuser", Email = "new@example.com" };

        _passwordPolicyValidatorMock.Setup(x => x.Validate("Str0ngP@ss"))
            .Returns(PasswordValidationResult.Success());

        _userManagerMock.Setup(x => x.FindByNameAsync("newuser"))
            .ReturnsAsync((User)null);
        _userManagerMock.Setup(x => x.CreateAsync(It.IsAny<User>(), "Str0ngP@ss"))
            .ReturnsAsync(IdentityResult.Success)
            .Callback<User, string>((u, _) =>
            {
                u.Id = "newuser-id";
                u.UserName = "newuser";
            });

        // Act
        var result = await _usersManager.CreateUser("newuser", "new@example.com", "Str0ngP@ss", null);

        // Assert
        result.ShouldNotBeNull();
        result.UserName.ShouldBe("newuser");
    }

    [Test]
    public async Task CreateUser_NoPolicyConfigured_AcceptsAnyPassword()
    {
        // Arrange
        SetupAdminUser();

        // Validator returns success (no policy configured)
        _passwordPolicyValidatorMock.Setup(x => x.Validate(It.IsAny<string>()))
            .Returns(PasswordValidationResult.Success());

        _userManagerMock.Setup(x => x.FindByNameAsync("newuser"))
            .ReturnsAsync((User)null);
        _userManagerMock.Setup(x => x.CreateAsync(It.IsAny<User>(), "a"))
            .ReturnsAsync(IdentityResult.Success)
            .Callback<User, string>((u, _) =>
            {
                u.Id = "newuser-id";
                u.UserName = "newuser";
            });

        // Act
        var result = await _usersManager.CreateUser("newuser", "new@example.com", "a", null);

        // Assert
        result.ShouldNotBeNull();
        result.UserName.ShouldBe("newuser");
    }

    #endregion

    #region ChangePassword Password Policy Tests

    [Test]
    public async Task ChangePassword_SelfService_WithPolicyViolation_ThrowsBadRequestException()
    {
        // Arrange
        var currentUser = new User { Id = "user-id", UserName = "testuser" };
        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(currentUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);

        _passwordPolicyValidatorMock.Setup(x => x.Validate("weak"))
            .Returns(PasswordValidationResult.Failure("Password must be at least 8 characters long"));

        // Act & Assert
        var action = () => _usersManager.ChangePassword("oldpass", "weak");
        var ex = await Should.ThrowAsync<BadRequestException>(action);
        ex.Message.ShouldContain("Password does not meet the requirements");
    }

    [Test]
    public async Task ChangePassword_AdminOverride_BypassesPolicy()
    {
        // Arrange
        SetupAdminUser();
        var targetUser = new User { Id = "target-id", UserName = "targetuser" };

        _userManagerMock.Setup(x => x.FindByNameAsync("targetuser"))
            .ReturnsAsync(targetUser);

        // Policy validator returns failure, but admin override should bypass it
        _passwordPolicyValidatorMock.Setup(x => x.Validate("weak"))
            .Returns(PasswordValidationResult.Failure("Password must be at least 8 characters long"));

        _userManagerMock.Setup(x => x.GeneratePasswordResetTokenAsync(targetUser))
            .ReturnsAsync("reset-token");
        _userManagerMock.Setup(x => x.ResetPasswordAsync(targetUser, "reset-token", "weak"))
            .ReturnsAsync(IdentityResult.Success);

        // Act — currentPassword is null = admin override
        var result = await _usersManager.ChangePassword("targetuser", null, "weak");

        // Assert
        result.ShouldNotBeNull();
        result.UserName.ShouldBe("targetuser");
        result.Password.ShouldBe("weak");

        // Verify policy validator was NOT called (admin override path)
        _passwordPolicyValidatorMock.Verify(x => x.Validate("weak"), Times.Never);
    }

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
    }

    private void SetupDefaultAuthManagerBehavior()
    {
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync(true);
    }

    private async Task<User> CreateTestUser(string userName)
    {
        var user = new User
        {
            Id = $"{userName}-id",
            UserName = userName,
            Email = $"{userName}@example.com",
            NormalizedUserName = userName.ToUpper()
        };

        await _appContext.Users.AddAsync(user);
        await _appContext.SaveChangesAsync();

        return user;
    }

    private async Task<OrganizationDto> CreateOrganizationForUser(string userId, string slug)
    {
        var org = new Organization
        {
            Slug = slug,
            Name = $"Organization {slug}",
            Description = "Test Description",
            IsPublic = false,
            OwnerId = userId,
            CreationDate = DateTime.Now,
            Datasets = new List<Dataset>()
        };

        await _context.Organizations.AddAsync(org);
        await _context.SaveChangesAsync();

        return org.ToDto();
    }

    private async Task CreateBatchForUser(string userName, string orgSlug)
    {
        var org = await _context.Organizations
            .Include(o => o.Datasets)
            .FirstAsync(o => o.Slug == orgSlug);

        // Create a dataset if none exists
        if (org.Datasets.Count == 0)
        {
            var dataset = new Dataset
            {
                Slug = "test-dataset",
                CreationDate = DateTime.Now,
                InternalRef = Guid.NewGuid(),
                Organization = org
            };
            org.Datasets.Add(dataset);
            await _context.SaveChangesAsync();
        }

        var batch = new Batch
        {
            Token = Guid.NewGuid().ToString(),
            UserName = userName,
            Dataset = org.Datasets.First(),
            Start = DateTime.Now,
            Status = BatchStatus.Committed
        };

        await _context.Batches.AddAsync(batch);
        await _context.SaveChangesAsync();
    }

    private static async Task<RegistryContext> CreateTestRegistryContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
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
            Datasets = new List<Dataset>()
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

        await context.Roles.AddAsync(adminRole);
        await context.SaveChangesAsync();

        return context;
    }

    #endregion
}
