using Shouldly;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Test.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Registry.Web.Exceptions;
using Registry.Web.Identity;

namespace Registry.Web.Test;

[TestFixture]
public class OrganizationMemberPermissionsTest : TestBase
{
    private Mock<IAuthManager> _authManagerMock;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IDatasetsManager> _datasetManagerMock;
    private Mock<IUtils> _utilsMock;
    private ILogger<OrganizationsManager> _logger;
    private RegistryContext _context;
    private ApplicationDbContext _appContext;
    private AppSettings _appSettings;

    private User _adminUser;
    private User _ownerUser;
    private User _memberUser;
    private User _readOnlyUser;
    private Organization _testOrg;

    [SetUp]
    public async Task Setup()
    {
        _authManagerMock = new Mock<IAuthManager>();
        _appSettings = new AppSettings { EnableOrganizationMemberManagement = true };
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _appSettingsMock.Setup(x => x.Value).Returns(_appSettings);
        _datasetManagerMock = new Mock<IDatasetsManager>();
        _utilsMock = new Mock<IUtils>();
        _logger = CreateTestLogger<OrganizationsManager>();

        _context = await CreateTestRegistryContext();
        _appContext = await CreateTestApplicationContext();

        await SetupTestData();

        // Set up default auth manager behavior
        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync(true);
    }

    private async Task SetupTestData()
    {
        // Create users
        _adminUser = new User { Id = "admin-id", UserName = "admin", Email = "admin@test.com" };
        _ownerUser = new User { Id = "owner-id", UserName = "owner", Email = "owner@test.com" };
        _memberUser = new User { Id = "member-id", UserName = "member", Email = "member@test.com" };
        _readOnlyUser = new User { Id = "readonly-id", UserName = "readonly", Email = "readonly@test.com" };

        _appContext.Users.AddRange(_adminUser, _ownerUser, _memberUser, _readOnlyUser);
        await _appContext.SaveChangesAsync();

        // Create organization
        _testOrg = new Organization
        {
            Slug = "test-org",
            Name = "Test Organization",
            OwnerId = _ownerUser.Id,
            CreationDate = DateTime.UtcNow,
            IsPublic = false
        };
        _context.Organizations.Add(_testOrg);
        await _context.SaveChangesAsync();

        // Add members with different permission levels
        _context.Set<OrganizationUser>().AddRange(
            new OrganizationUser
            {
                OrganizationSlug = _testOrg.Slug,
                UserId = _memberUser.Id,
                Permissions = OrganizationPermissions.ReadWrite,
                GrantedAt = DateTime.UtcNow,
                GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _testOrg.Slug,
                UserId = _readOnlyUser.Id,
                Permissions = OrganizationPermissions.ReadOnly,
                GrantedAt = DateTime.UtcNow,
                GrantedBy = _ownerUser.Id
            }
        );
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        await _appContext.DisposeAsync();
    }

    #region Permission Level Tests

    [Test]
    public void OrganizationPermission_ReadOnly_AllowsOnlyRead()
    {
        const OrganizationPermissions permission = OrganizationPermissions.ReadOnly;

        permission.HasAccess(AccessType.Read).ShouldBeTrue();
        permission.HasAccess(AccessType.Write).ShouldBeFalse();
        permission.HasAccess(AccessType.Delete).ShouldBeFalse();
        permission.CanManageMembers().ShouldBeFalse();
    }

    [Test]
    public void OrganizationPermission_ReadWrite_AllowsReadAndWrite()
    {
        const OrganizationPermissions permission = OrganizationPermissions.ReadWrite;

        permission.HasAccess(AccessType.Read).ShouldBeTrue();
        permission.HasAccess(AccessType.Write).ShouldBeTrue();
        permission.HasAccess(AccessType.Delete).ShouldBeFalse();
        permission.CanManageMembers().ShouldBeFalse();
    }

    [Test]
    public void OrganizationPermission_ReadWriteDelete_AllowsReadWriteDelete()
    {
        const OrganizationPermissions permission = OrganizationPermissions.ReadWriteDelete;

        permission.HasAccess(AccessType.Read).ShouldBeTrue();
        permission.HasAccess(AccessType.Write).ShouldBeTrue();
        permission.HasAccess(AccessType.Delete).ShouldBeTrue();
        permission.CanManageMembers().ShouldBeFalse();
    }

    [Test]
    public void OrganizationPermission_Admin_AllowsEverything()
    {
        var permission = OrganizationPermissions.Admin;

        permission.HasAccess(AccessType.Read).ShouldBeTrue();
        permission.HasAccess(AccessType.Write).ShouldBeTrue();
        permission.HasAccess(AccessType.Delete).ShouldBeTrue();
        permission.CanManageMembers().ShouldBeTrue();
    }

    [Test]
    public void OrganizationPermission_GetDisplayName_ReturnsCorrectNames()
    {
        OrganizationPermissions.ReadOnly.GetDisplayName().ShouldBe("Read Only");
        OrganizationPermissions.ReadWrite.GetDisplayName().ShouldBe("Read/Write");
        OrganizationPermissions.ReadWriteDelete.GetDisplayName().ShouldBe("Read/Write/Delete");
        OrganizationPermissions.Admin.GetDisplayName().ShouldBe("Admin");
    }

    #endregion

    #region GetMembers Tests

    [Test]
    public async Task GetMembers_AsOwner_ReturnsAllMembers()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        var members = (await manager.GetMembers(_testOrg.Slug)).ToList();

        members.Count.ShouldBe(2);
        members.ShouldContain(m => m.UserName == "member" && m.Permissions == OrganizationPermissions.ReadWrite);
        members.ShouldContain(m => m.UserName == "readonly" && m.Permissions == OrganizationPermissions.ReadOnly);
    }

    [Test]
    public async Task GetMembers_AsAdmin_ReturnsAllMembers()
    {
        SetupAsAdmin(_adminUser);
        var manager = CreateManager();

        var members = (await manager.GetMembers(_testOrg.Slug)).ToList();

        members.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetMembers_AsMember_ReturnsAllMembers()
    {
        SetupAsUser(_memberUser);
        var manager = CreateManager();

        var members = (await manager.GetMembers(_testOrg.Slug)).ToList();

        members.Count.ShouldBe(2);
    }

    [Test]
    public async Task GetMembers_NonExistentOrg_ThrowsNotFoundException()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        var action = () => manager.GetMembers("non-existent-org");
        await Should.ThrowAsync<NotFoundException>(action);
    }

    #endregion

    #region AddMember Tests

    [Test]
    public async Task AddMember_AsOwner_Succeeds()
    {
        var newUser = new User { Id = "new-user-id", UserName = "newuser", Email = "new@test.com" };
        _appContext.Users.Add(newUser);
        await _appContext.SaveChangesAsync();

        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await manager.AddMember(_testOrg.Slug, newUser.UserName, OrganizationPermissions.ReadWrite);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == newUser.Id && u.OrganizationSlug == _testOrg.Slug);

        orgUser.ShouldNotBeNull();
        orgUser.Permissions.ShouldBe(OrganizationPermissions.ReadWrite);
        orgUser.GrantedBy.ShouldBe(_ownerUser.Id);
    }

    [Test]
    public async Task AddMember_WhenFeatureDisabled_ThrowsException()
    {
        _appSettings.EnableOrganizationMemberManagement = false;
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            manager.AddMember(_testOrg.Slug, "some-user-id", OrganizationPermissions.ReadWrite)
        );

        ex.Message.ShouldContain("disabled");
    }

    [Test]
    public async Task AddMember_AsReadOnlyMember_ThrowsUnauthorized()
    {
        SetupAsUser(_readOnlyUser);
        var manager = CreateManager();

        await Should.ThrowAsync<UnauthorizedException>(
            manager.AddMember(_testOrg.Slug, "some-user-id", OrganizationPermissions.ReadWrite)
        );
    }

    [Test]
    public async Task AddMember_OwnerAsTarget_ThrowsException()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await Should.ThrowAsync<InvalidOperationException>(
            manager.AddMember(_testOrg.Slug, _ownerUser.UserName, OrganizationPermissions.ReadWrite)
        );
    }

    [Test]
    public async Task AddMember_AlreadyMember_ThrowsConflict()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await Should.ThrowAsync<ConflictException>(
            manager.AddMember(_testOrg.Slug, _memberUser.UserName, OrganizationPermissions.ReadWrite)
        );
    }

    #endregion

    #region UpdateMemberPermission Tests

    [Test]
    public async Task UpdateMemberPermission_AsOwner_Succeeds()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await manager.UpdateMemberPermission(_testOrg.Slug, _memberUser.UserName, OrganizationPermissions.Admin);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == _memberUser.Id);

        orgUser.Permissions.ShouldBe(OrganizationPermissions.Admin);
    }

    [Test]
    public async Task UpdateMemberPermission_AsAdmin_Succeeds()
    {
        SetupAsAdmin(_adminUser);
        var manager = CreateManager();

        await manager.UpdateMemberPermission(_testOrg.Slug, _memberUser.UserName, OrganizationPermissions.ReadWriteDelete);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == _memberUser.Id);

        orgUser.Permissions.ShouldBe(OrganizationPermissions.ReadWriteDelete);
    }

    [Test]
    public async Task UpdateMemberPermission_NonMember_ThrowsNotFound()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            manager.UpdateMemberPermission(_testOrg.Slug, "non-existent-user", OrganizationPermissions.ReadWrite)
        );
    }

    #endregion

    #region RemoveMember Tests

    [Test]
    public async Task RemoveMember_AsOwner_Succeeds()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await manager.RemoveMember(_testOrg.Slug, _memberUser.UserName);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == _memberUser.Id);

        orgUser.ShouldBeNull();
    }

    [Test]
    public async Task RemoveMember_NonExistentUser_ThrowsNotFound()
    {
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await Should.ThrowAsync<NotFoundException>(
            manager.RemoveMember(_testOrg.Slug, "non-existent-user")
        );
    }

    [Test]
    public async Task RemoveMember_WhenFeatureDisabled_ThrowsException()
    {
        _appSettings.EnableOrganizationMemberManagement = false;
        SetupAsUser(_ownerUser);
        var manager = CreateManager();

        await Should.ThrowAsync<InvalidOperationException>(
            manager.RemoveMember(_testOrg.Slug, _memberUser.UserName)
        );
    }

    #endregion

    #region IsMemberManagementEnabled Tests

    [Test]
    public void IsMemberManagementEnabled_WhenEnabled_ReturnsTrue()
    {
        _appSettings.EnableOrganizationMemberManagement = true;
        var manager = CreateManager();

        manager.IsMemberManagementEnabled.ShouldBeTrue();
    }

    [Test]
    public void IsMemberManagementEnabled_WhenDisabled_ReturnsFalse()
    {
        _appSettings.EnableOrganizationMemberManagement = false;
        var manager = CreateManager();

        manager.IsMemberManagementEnabled.ShouldBeFalse();
    }

    #endregion

    #region Helper Methods

    private OrganizationsManager CreateManager()
    {
        return new OrganizationsManager(
            _authManagerMock.Object,
            _context,
            _utilsMock.Object,
            _datasetManagerMock.Object,
            _appContext,
            _appSettingsMock.Object,
            _logger
        );
    }

    private void SetupAsUser(User user)
    {
        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(user);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
    }

    private void SetupAsAdmin(User user)
    {
        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(user);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
    }

    private static async Task<RegistryContext> CreateTestRegistryContext()
    {
        var options = new DbContextOptionsBuilder<RegistryContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryDatabase-{Guid.NewGuid()}")
            .Options;

        var context = new RegistryContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static async Task<ApplicationDbContext> CreateTestApplicationContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: $"RegistryAppDatabase-{Guid.NewGuid()}")
            .Options;

        var context = new ApplicationDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    #endregion
}
