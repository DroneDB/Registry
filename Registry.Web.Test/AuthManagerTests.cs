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
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

[TestFixture]
public class AuthManagerTests
{
    private Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<UserManager<User>> _userManagerMock;
    private Mock<ILogger<AuthManager>> _loggerMock;
    private Mock<RegistryContext> _context;

    private AuthManager _authManager;
    private User _normalUser;
    private User _randomUser;
    private User _adminUser;
    private User _deactivatedUser;
    private Organization _publicOrg;
    private Organization _privateOrg;
    private Dataset _publicDataset;
    private Dataset _privateDataset;

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

        // Setup AuthManager
        _authManager = new AuthManager(
            _userManagerMock.Object,
            _httpContextAccessorMock.Object,
            _ddbManagerMock.Object,
            _context.Object,
            _loggerMock.Object
        );

        // Setup DDB mock for dataset metadata
        var ddbMock = new Mock<IDDB>();

        ddbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry
            {
                Properties = new Dictionary<string, object>
                {
                    { "public", true },
                    { "name", "public" }
                },
                Size = 1000,
                ModifiedTime = DateTime.Now
            }
        ));

        var metaManagerMock = new Mock<IMetaManager>();
        metaManagerMock.Setup(x => x.Get<int>(SafeMetaManager.VisibilityField, null))
            .Returns((int)Visibility.Public);

        ddbMock.Setup(x => x.Meta).Returns(metaManagerMock.Object);

        _ddbManagerMock.Setup(x => x.Get(_publicOrg.Slug, _publicDataset.InternalRef))
            .Returns(ddbMock.Object);

        var privateDdbMock = new Mock<IDDB>();

        privateDdbMock.Setup(x => x.GetInfoAsync(default)).Returns(Task.FromResult(new Entry
            {
                Properties = new Dictionary<string, object>
                {
                    { "public", false },
                    { "name", "ds" }
                },
                Size = 1000,
                ModifiedTime = DateTime.Now
            }
        ));

        var privateMetaManagerMock = new Mock<IMetaManager>();
        privateMetaManagerMock.Setup(x => x.Get<int>(SafeMetaManager.VisibilityField, null))
            .Returns((int)Visibility.Private);

        privateDdbMock.Setup(x => x.Meta).Returns(privateMetaManagerMock.Object);

        _ddbManagerMock.Setup(x => x.Get(_privateOrg.Slug, _privateDataset.InternalRef))
            .Returns(privateDdbMock.Object);
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

    public async Task CanListOrganizations_AnonymousUser_Denied()
    {
        // Arrange
        SetupCurrentUser(null);

        // Act
        var result = await _authManager.CanListOrganizations();

        // Assert
        result.Should().BeFalse();
    }

    public async Task CanListOrganizations_DeactivatedUser_Denied()
    {
        // Arrange
        SetupCurrentUser(_deactivatedUser);

        // Act
        var result = await _authManager.CanListOrganizations();

        // Assert
        result.Should().BeFalse();
    }

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
    }
}