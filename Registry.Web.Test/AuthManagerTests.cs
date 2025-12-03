using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

[TestFixture]
public class AuthManagerTests : TestBase
{
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<UserManager<User>> _userManagerMock;
    private Mock<ILogger<AuthManager>> _loggerMock;
    private Mock<RegistryContext> _context;
    private Mock<ICacheManager> _cacheManagerMock;

    private AuthManager _authManager;
    private User _normalUser;
    private User _randomUser;
    private User _adminUser;
    private User _deactivatedUser;
    private Organization _publicOrg;
    private Organization _privateOrg;
    private Dataset _publicDataset;
    private Dataset _privateDataset;
    private Mock<IDDB> _publicDdbMock;
    private Mock<IDDB> _privateDdbMock;

    #region Setup

    [SetUp]
    public void Setup()
    {
        // Setup mock users
        _normalUser = new User { Id = "user1", UserName = "normal" };
        _randomUser = new User { Id = "random1", UserName = "random" };
        _adminUser = new User { Id = "admin1", UserName = "admin" };
        _deactivatedUser = new User { Id = "deactivated1", UserName = "deactivated" };

        // Setup organizations
        _publicOrg = new Organization
        {
            Slug = "public-org",
            IsPublic = true,
            OwnerId = _normalUser.Id,
            Users = new List<OrganizationUser>()
        };

        _privateOrg = new Organization
        {
            Slug = "private-org",
            IsPublic = false,
            OwnerId = _normalUser.Id,
            Users = new List<OrganizationUser>()
        };

        // Setup datasets
        _publicDataset = new Dataset
        {
            Id = 1,
            Slug = "public-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _privateDataset = new Dataset
        {
            Id = 2,
            Slug = "private-dataset",
            Organization = _privateOrg,
            InternalRef = Guid.NewGuid()
        };

        // Setup mocks
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _ddbManagerMock = new Mock<IDdbManager>();
        _userManagerMock = MockUserManager();
        _loggerMock = new Mock<ILogger<AuthManager>>();
        _context = new Mock<RegistryContext>();
        _cacheManagerMock = new Mock<ICacheManager>();

        // Setup DDB mocks for dataset metadata
        _publicDdbMock = new Mock<IDDB>();
        _publicDdbMock.Setup(x => x.GetInfo(null)).Returns(new Entry
            {
                Properties = new Dictionary<string, object>
                {
                    { "public", true },
                    { "name", "public" }
                },
                Size = 1000,
                ModifiedTime = DateTime.Now
            }
        );

        var publicMetaManagerMock = new Mock<IMetaManager>();
        publicMetaManagerMock.Setup(x => x.Get<int>(SafeMetaManager.VisibilityField, null))
            .Returns((int)Visibility.Public);
        _publicDdbMock.Setup(x => x.Meta).Returns(publicMetaManagerMock.Object);

        _privateDdbMock = new Mock<IDDB>();
        _privateDdbMock.Setup(x => x.GetInfo(null)).Returns(new Entry
            {
                Properties = new Dictionary<string, object>
                {
                    { "public", false },
                    { "name", "ds" }
                },
                Size = 1000,
                ModifiedTime = DateTime.Now
            }
        );

        var privateMetaManagerMock = new Mock<IMetaManager>();
        privateMetaManagerMock.Setup(x => x.Get<int>(SafeMetaManager.VisibilityField, null))
            .Returns((int)Visibility.Private);
        _privateDdbMock.Setup(x => x.Meta).Returns(privateMetaManagerMock.Object);

        // Setup DdbManager to return appropriate IDDB instances
        _ddbManagerMock.Setup(x => x.Get(_publicOrg.Slug, _publicDataset.InternalRef))
            .Returns(_publicDdbMock.Object);
        _ddbManagerMock.Setup(x => x.Get(_privateOrg.Slug, _privateDataset.InternalRef))
            .Returns(_privateDdbMock.Object);

        // Setup cache manager to return visibility from DDB
        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(_publicDataset.InternalRef) &&
                    args[2] == _ddbManagerMock.Object)))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Public));

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _privateOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_privateOrg.Slug) &&
                    args[1].Equals(_privateDataset.InternalRef) &&
                    args[2] == _ddbManagerMock.Object)))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Private));

        // Setup AuthManager
        _authManager = new AuthManager(
            _userManagerMock.Object,
            _httpContextAccessorMock.Object,
            _ddbManagerMock.Object,
            _context.Object,
            _loggerMock.Object,
            _cacheManagerMock.Object
        );
    }

    private Mock<UserManager<User>> MockUserManager()
    {
        var store = new Mock<IUserStore<User>>();
        var userManager =
            new Mock<UserManager<User>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        userManager.Setup(x => x.FindByIdAsync(It.IsAny<string>()))
            .Returns((string id) => Task.FromResult(new[] { _normalUser, _adminUser, _deactivatedUser, _randomUser }
                .FirstOrDefault(u => u.Id == id)));

        userManager.Setup(x => x.IsInRoleAsync(_adminUser, ApplicationDbContext.AdminRoleName))
            .ReturnsAsync(true);

        userManager.Setup(x => x.IsInRoleAsync(_deactivatedUser, ApplicationDbContext.DeactivatedRoleName))
            .ReturnsAsync(true);

        return userManager;
    }

    private void SetupCurrentUser(User user)
    {
        if (user != null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Id)
            };
            var identity = new ClaimsIdentity(claims);
            var principal = new ClaimsPrincipal(identity);
            var context = new DefaultHttpContext { User = principal };
            _httpContextAccessorMock.Setup(x => x.HttpContext).Returns(context);
        }
        else
        {
            _httpContextAccessorMock.Setup(x => x.HttpContext).Returns((HttpContext)null);
        }
    }

    #endregion

    #region Cache Interaction Tests

    [Test]
    public async Task RequestAccess_PublicDataset_VerifiesCacheInteraction()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_publicDataset, AccessType.Read);

        // Assert
        result.Should().BeTrue();

        // Verify cache was called with correct parameters
        _cacheManagerMock.Verify(x => x.GetAsync(
            MagicStrings.DatasetVisibilityCacheSeed,
            _publicOrg.Slug,
            It.Is<object[]>(args =>
                args.Length == 3 &&
                args[0].Equals(_publicOrg.Slug) &&
                args[1].Equals(_publicDataset.InternalRef) &&
                args[2] == _ddbManagerMock.Object)),
            Times.Once);
    }

    [Test]
    public async Task RequestAccess_PrivateDataset_VerifiesCacheInteraction()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_privateDataset, AccessType.Read);

        // Assert
        result.Should().BeFalse();

        // Verify cache was called with correct parameters
        _cacheManagerMock.Verify(x => x.GetAsync(
            MagicStrings.DatasetVisibilityCacheSeed,
            _privateOrg.Slug,
            It.Is<object[]>(args =>
                args.Length == 3 &&
                args[0].Equals(_privateOrg.Slug) &&
                args[1].Equals(_privateDataset.InternalRef) &&
                args[2] == _ddbManagerMock.Object)),
            Times.Once);
    }

    [Test]
    public async Task RequestAccess_Dataset_Owner_VerifiesUserManagerCalls()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var result = await _authManager.RequestAccess(_privateDataset, AccessType.Write);

        // Assert
        result.Should().BeTrue();

        // Verify UserManager interactions
        _userManagerMock.Verify(x => x.FindByIdAsync(_normalUser.Id), Times.AtLeastOnce);
        _userManagerMock.Verify(x => x.IsInRoleAsync(_normalUser, ApplicationDbContext.AdminRoleName), Times.Once);
        _userManagerMock.Verify(x => x.IsInRoleAsync(_normalUser, ApplicationDbContext.DeactivatedRoleName), Times.Once);
    }

    [Test]
    public async Task RequestAccess_Dataset_Admin_VerifiesAdminCheck()
    {
        // Arrange
        SetupCurrentUser(_adminUser);

        // Act
        var result = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        result.Should().BeTrue();

        // Verify admin check was performed
        _userManagerMock.Verify(x => x.IsInRoleAsync(_adminUser, ApplicationDbContext.AdminRoleName), Times.Once);
        // Should not check deactivated since admin has full access
        _userManagerMock.Verify(x => x.IsInRoleAsync(_adminUser, ApplicationDbContext.DeactivatedRoleName), Times.Once);
    }

    [Test]
    public async Task RequestAccess_Dataset_DeactivatedUser_VerifiesDeactivatedCheck()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var result = await _authManager.RequestAccess(_privateDataset, AccessType.Read);

        // Assert
        result.Should().BeFalse();

        // Verify deactivated check was performed early
        _userManagerMock.Verify(x => x.IsInRoleAsync(_deactivatedUser, ApplicationDbContext.DeactivatedRoleName), Times.Once);
    }

    [Test]
    public async Task RequestAccess_Dataset_VerifiesDdbManagerNotCalledDirectly()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var result = await _authManager.RequestAccess(_publicDataset, AccessType.Read);

        // Assert
        result.Should().BeTrue();

        // DdbManager.Get should not be called directly in the test flow
        // It should only be called through cache provider
        _ddbManagerMock.Verify(x => x.Get(_publicOrg.Slug, _publicDataset.InternalRef), Times.Never);
    }

    [Test]
    public async Task RequestAccess_Dataset_InvalidCacheData_HandlesGracefully()
    {
        // Arrange
        var invalidDataset = new Dataset
        {
            Id = 99,
            Slug = "invalid-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        // Setup cache to return invalid data (less than 4 bytes)
        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(invalidDataset.InternalRef))))
            .ReturnsAsync(new byte[2]); // Invalid: less than sizeof(int)

        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(invalidDataset, AccessType.Read);

        // Assert
        // Should default to Private visibility and deny access
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_AnonymousUser_ReadAccess_Allowed()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        // Setup cache to return Unlisted visibility
        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(unlistedDataset, AccessType.Read);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Unlisted Dataset Tests

    [Test]
    public async Task RequestAccess_UnlistedDataset_AnonymousUser_WriteAccess_Denied()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(unlistedDataset, AccessType.Write);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_AnonymousUser_DeleteAccess_Denied()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(unlistedDataset, AccessType.Delete);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_Owner_AllAccess_Allowed()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(_normalUser);

        // Act
        var readResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_Admin_AllAccess_Allowed()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(_adminUser);

        // Act
        var readResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_RandomUser_ReadAccess_Allowed()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(_randomUser);

        // Act
        var result = await _authManager.RequestAccess(unlistedDataset, AccessType.Read);

        // Assert - Random user can read unlisted datasets (like public)
        result.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_RandomUser_WriteAccess_Denied()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(_randomUser);

        // Act
        var result = await _authManager.RequestAccess(unlistedDataset, AccessType.Write);

        // Assert - Random user cannot write to unlisted datasets
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_DeactivatedUser_AllAccess_Denied()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(_deactivatedUser);

        // Act
        var readResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeFalse();
        writeResult.Should().BeFalse();
        deleteResult.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_OrganizationMember_ReadWriteAllowed_DeleteDenied()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _privateOrg, // Use private org to test member access
            InternalRef = Guid.NewGuid()
        };

        var orgMember = new User { Id = "member1", UserName = "member" };
        _privateOrg.Users.Add(new OrganizationUser { UserId = orgMember.Id });

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _privateOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_privateOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(orgMember);

        _userManagerMock.Setup(x => x.FindByIdAsync(orgMember.Id))
            .ReturnsAsync(orgMember);

        // Act
        var readResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(unlistedDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_UnlistedDataset_DeactivatedOwner_AnonymousUser_ReadAccess_Denied()
    {
        // Arrange
        var unlistedOrg = new Organization
        {
            Slug = "unlisted-org",
            IsPublic = true,
            OwnerId = _deactivatedUser.Id,
            Users = new List<OrganizationUser>()
        };

        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = unlistedOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                unlistedOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(unlistedOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(unlistedDataset, AccessType.Read);

        // Assert - Should be denied because owner is deactivated
        result.Should().BeFalse();

        // Verify owner was checked
        _userManagerMock.Verify(x => x.FindByIdAsync(_deactivatedUser.Id), Times.Once);
        _userManagerMock.Verify(x => x.IsInRoleAsync(_deactivatedUser, ApplicationDbContext.DeactivatedRoleName), Times.Once);
    }

    #endregion

    #region Organization Access Tests

    [Test]
    public async Task RequestAccess_PublicOrganization_AnonymousUser_ReadAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_publicOrg, AccessType.Read);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_PrivateOrganization_AnonymousUser_ReadAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_privateOrg, AccessType.Read);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_Organization_Owner_AllAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateOrg, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateOrg, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateOrg, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_Organization_Admin_AllAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(_adminUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateOrg, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateOrg, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateOrg, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_Organization_DeactivatedUser_AllAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var readResult = await _authManager.RequestAccess(_publicOrg, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_publicOrg, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_publicOrg, AccessType.Delete);

        // Assert
        readResult.Should().BeFalse();
        writeResult.Should().BeFalse();
        deleteResult.Should().BeFalse();
    }

    [Test]
    public async Task CanListOrganizations_AnonymousUser_Denied()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.CanListOrganizations();

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task CanListOrganizations_DeactivatedUser_Denied()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var result = await _authManager.CanListOrganizations();

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task CanListOrganizations_NormalUser_Allowed()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var result = await _authManager.CanListOrganizations();

        // Assert
        result.Should().BeTrue();
    }


    [Test]
    public async Task RequestAccess_PublicOrganization_DeactivatedOwner_AnonymousUser_ReadAccess_Denied()
    {
        // Arrange
        _publicOrg.OwnerId = _deactivatedUser.Id;
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_publicOrg, AccessType.Read);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_PrivateOrganization_RandomUser_ReadAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(_randomUser);

        // Act
        var result = await _authManager.RequestAccess(_privateOrg, AccessType.Read);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_PrivateOrganization_DeactivatedUser_AllAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateOrg, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateOrg, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateOrg, AccessType.Delete);

        // Assert
        readResult.Should().BeFalse();
        writeResult.Should().BeFalse();
        deleteResult.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_PublicOrganization_RegularUser_ReadAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var result = await _authManager.RequestAccess(_publicOrg, AccessType.Read);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Dataset Access Tests

    [Test]
    public async Task RequestAccess_PublicDataset_AnonymousUser_ReadAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_publicDataset, AccessType.Read);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_PrivateDataset_AnonymousUser_ReadAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_privateDataset, AccessType.Read);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_Dataset_Owner_AllAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_Dataset_Admin_AllAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(_adminUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_Dataset_Admin_DeactivatedOrganization_Allowed()
    {
        // Arrange
        SetupCurrentUser(_adminUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeTrue();
    }


    [Test]
    public async Task RequestAccess_Dataset_DeactivatedUser_AllAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeFalse();
        writeResult.Should().BeFalse();
        deleteResult.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_Dataset_OrganizationMember_ReadWriteAllowed_DeleteDenied()
    {
        // Arrange
        var orgMember = new User { Id = "member1", UserName = "member" };
        _privateOrg.Users.Add(new OrganizationUser { UserId = orgMember.Id });
        SetupCurrentUser(orgMember);

        _userManagerMock.Setup(x => x.FindByIdAsync(orgMember.Id))
            .ReturnsAsync(orgMember);

        // Act
        var readResult = await _authManager.RequestAccess(_privateDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeTrue();
        writeResult.Should().BeTrue();
        deleteResult.Should().BeFalse();
    }

    #endregion

    #region Additional Dataset Access Tests

    [Test]
    public async Task RequestAccess_PublicDataset_DeactivatedOwner_AnonymousUser_ReadAccess_Denied()
    {
        // Arrange
        _publicOrg.OwnerId = _deactivatedUser.Id;
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.RequestAccess(_publicDataset, AccessType.Read);

        // Assert
        result.Should().BeFalse();

        // Verify owner was checked for deactivation
        _userManagerMock.Verify(x => x.FindByIdAsync(_deactivatedUser.Id), Times.Once);
        _userManagerMock.Verify(x => x.IsInRoleAsync(_deactivatedUser, ApplicationDbContext.DeactivatedRoleName), Times.Once);
    }

    [Test]
    public async Task RequestAccess_PrivateDataset_RandomUser_ReadAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(_randomUser);

        // Act
        var result = await _authManager.RequestAccess(_privateDataset, AccessType.Read);

        // Assert
        result.Should().BeFalse();

        // Verify user checks were performed
        _userManagerMock.Verify(x => x.IsInRoleAsync(_randomUser, ApplicationDbContext.DeactivatedRoleName), Times.Once);
        _userManagerMock.Verify(x => x.IsInRoleAsync(_randomUser, ApplicationDbContext.AdminRoleName), Times.Once);
    }

    [Test]
    public async Task RequestAccess_PrivateDataset_DeactivatedUser_AllAccess_Denied()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var readResult = await _authManager.RequestAccess(_privateDataset, AccessType.Read);
        var writeResult = await _authManager.RequestAccess(_privateDataset, AccessType.Write);
        var deleteResult = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        readResult.Should().BeFalse();
        writeResult.Should().BeFalse();
        deleteResult.Should().BeFalse();
    }

    [Test]
    public async Task RequestAccess_PublicDataset_RegularUser_ReadAccess_Allowed()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var result = await _authManager.RequestAccess(_publicDataset, AccessType.Read);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task RequestAccess_PrivateDataset_OrganizationMember_ReadAccess_Allowed()
    {
        // Arrange
        var orgMember = new User { Id = "member1", UserName = "member" };
        _privateOrg.Users.Add(new OrganizationUser { UserId = orgMember.Id });
        SetupCurrentUser(orgMember);

        _userManagerMock.Setup(x => x.FindByIdAsync(orgMember.Id))
            .ReturnsAsync(orgMember);

        // Act
        var readResult = await _authManager.RequestAccess(_privateDataset, AccessType.Read);

        // Assert
        readResult.Should().BeTrue();

        // Verify member checks
        _userManagerMock.Verify(x => x.FindByIdAsync(orgMember.Id), Times.AtLeastOnce);
        _userManagerMock.Verify(x => x.IsInRoleAsync(orgMember, ApplicationDbContext.DeactivatedRoleName), Times.AtLeastOnce);
    }

    [Test]
    public async Task RequestAccess_PrivateDataset_OrganizationMember_DeleteAccess_Denied()
    {
        // Arrange
        var orgMember = new User { Id = "member1", UserName = "member" };
        _privateOrg.Users.Add(new OrganizationUser { UserId = orgMember.Id });
        SetupCurrentUser(orgMember);

        _userManagerMock.Setup(x => x.FindByIdAsync(orgMember.Id))
            .ReturnsAsync(orgMember);

        // Act
        var deleteResult = await _authManager.RequestAccess(_privateDataset, AccessType.Delete);

        // Assert
        deleteResult.Should().BeFalse();

        // Verify that checks were still performed
        _userManagerMock.Verify(x => x.IsInRoleAsync(orgMember, ApplicationDbContext.AdminRoleName), Times.Once);
    }

    [Test]
    public async Task RequestAccess_Dataset_VerifiesCallHierarchy()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act - Make multiple access checks to verify proper call flow
        await _authManager.RequestAccess(_publicDataset, AccessType.Read);
        await _authManager.RequestAccess(_privateDataset, AccessType.Write);

        // Assert - Verify the entire call stack
        // 1. GetCurrentUser should be called
        _httpContextAccessorMock.Verify(x => x.HttpContext, Times.AtLeast(2));

        // 2. Cache should be queried for dataset visibility
        _cacheManagerMock.Verify(x => x.GetAsync(
            MagicStrings.DatasetVisibilityCacheSeed,
            It.IsAny<string>(),
            It.IsAny<object[]>()),
            Times.Exactly(2));

        // 3. User roles should be checked
        _userManagerMock.Verify(x => x.IsInRoleAsync(_normalUser, It.IsAny<string>()), Times.AtLeast(2));
    }

    #endregion

    #region GetDatasetPermissions Tests

    [Test]
    public async Task GetDatasetPermissions_AnonymousUser_PublicDataset_ReturnsReadOnly()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_publicDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeTrue();
        permissions.CanWrite.Should().BeFalse();
        permissions.CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task GetDatasetPermissions_AnonymousUser_PrivateDataset_ReturnsNoAccess()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_privateDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeFalse();
        permissions.CanWrite.Should().BeFalse();
        permissions.CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task GetDatasetPermissions_Owner_PrivateDataset_ReturnsFullAccess()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_privateDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeTrue();
        permissions.CanWrite.Should().BeTrue();
        permissions.CanDelete.Should().BeTrue();
    }

    [Test]
    public async Task GetDatasetPermissions_Admin_PrivateDataset_ReturnsFullAccess()
    {
        // Arrange
        SetupCurrentUser(_adminUser);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_privateDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeTrue();
        permissions.CanWrite.Should().BeTrue();
        permissions.CanDelete.Should().BeTrue();
    }

    [Test]
    public async Task GetDatasetPermissions_OrganizationMember_PrivateDataset_ReturnsReadWrite()
    {
        // Arrange
        var orgMember = new User { Id = "member1", UserName = "member" };
        _privateOrg.Users.Add(new OrganizationUser { UserId = orgMember.Id });
        SetupCurrentUser(orgMember);

        _userManagerMock.Setup(x => x.FindByIdAsync(orgMember.Id))
            .ReturnsAsync(orgMember);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_privateDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeTrue();
        permissions.CanWrite.Should().BeTrue();
        permissions.CanDelete.Should().BeFalse(); // Members cannot delete
    }

    [Test]
    public async Task GetDatasetPermissions_DeactivatedUser_PrivateDataset_ReturnsNoAccess()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_privateDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeFalse();
        permissions.CanWrite.Should().BeFalse();
        permissions.CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task GetDatasetPermissions_RandomUser_PrivateDataset_ReturnsNoAccess()
    {
        // Arrange
        SetupCurrentUser(_randomUser);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_privateDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeFalse();
        permissions.CanWrite.Should().BeFalse();
        permissions.CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task GetDatasetPermissions_RandomUser_PublicDataset_ReturnsReadOnly()
    {
        // Arrange
        SetupCurrentUser(_randomUser);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_publicDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeTrue();
        permissions.CanWrite.Should().BeFalse();
        permissions.CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task GetDatasetPermissions_AnonymousUser_UnlistedDataset_ReturnsReadOnly()
    {
        // Arrange
        var unlistedDataset = new Dataset
        {
            Id = 3,
            Slug = "unlisted-dataset",
            Organization = _publicOrg,
            InternalRef = Guid.NewGuid()
        };

        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                _publicOrg.Slug,
                It.Is<object[]>(args =>
                    args.Length == 3 &&
                    args[0].Equals(_publicOrg.Slug) &&
                    args[1].Equals(unlistedDataset.InternalRef))))
            .ReturnsAsync(BitConverter.GetBytes((int)Visibility.Unlisted));

        SetupCurrentUser(null);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(unlistedDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeTrue();
        permissions.CanWrite.Should().BeFalse();
        permissions.CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task GetDatasetPermissions_Owner_PublicDataset_ReturnsFullAccess()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_publicDataset);

        // Assert
        permissions.Should().NotBeNull();
        permissions.CanRead.Should().BeTrue();
        permissions.CanWrite.Should().BeTrue();
        permissions.CanDelete.Should().BeTrue();
    }

    [Test]
    public async Task GetDatasetPermissions_VerifiesAllAccessTypesCalled()
    {
        // Arrange
        SetupCurrentUser(_normalUser);

        // Act
        var permissions = await _authManager.GetDatasetPermissions(_privateDataset);

        // Assert
        permissions.Should().NotBeNull();

        // Verify cache was called three times (once for each access type)
        _cacheManagerMock.Verify(x => x.GetAsync(
            MagicStrings.DatasetVisibilityCacheSeed,
            _privateOrg.Slug,
            It.IsAny<object[]>()),
            Times.Exactly(3));
    }

    #endregion
}