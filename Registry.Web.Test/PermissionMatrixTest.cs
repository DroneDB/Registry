using Shouldly;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Auth;
using Registry.Test.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Exceptions;

namespace Registry.Web.Test;

/// <summary>
/// Comprehensive permission matrix tests covering all combinations of:
/// - User roles: Anonymous, Non-member, ReadOnly, ReadWrite, ReadWriteDelete, Admin member, Owner, System Admin
/// - Organization visibility: Public, Private
/// - Dataset visibility: Private, Unlisted, Public
/// - Access types: Read, Write, Delete
/// </summary>
[TestFixture]
public class PermissionMatrixTest : TestBase
{
    private RegistryContext _context;
    private ApplicationDbContext _appContext;
    private Mock<UserManager<User>> _userManagerMock;
    private Mock<IDdbManager> _ddbManagerMock;
    private Mock<ICacheManager> _cacheManagerMock;
    private ILogger<DatasetAccessControl> _datasetLogger;
    private ILogger<OrganizationAccessControl> _orgLogger;

    // Users
    private User _ownerUser;
    private User _nonMemberUser;
    private User _readOnlyMember;
    private User _readWriteMember;
    private User _readWriteDeleteMember;
    private User _adminMember;
    private User _systemAdminUser;
    private User _deactivatedMember;
    private User _deactivatedOwner;

    // Organizations
    private Organization _privateOrg;
    private Organization _publicOrg;
    private Organization _deactivatedOwnerOrg;

    // Datasets (per org, per visibility)
    private Dataset _privateOrgPrivateDs;
    private Dataset _privateOrgUnlistedDs;
    private Dataset _privateOrgPublicDs;
    private Dataset _publicOrgPrivateDs;
    private Dataset _publicOrgUnlistedDs;
    private Dataset _publicOrgPublicDs;
    private Dataset _deactivatedOwnerOrgPublicDs;

    [SetUp]
    public async Task Setup()
    {
        _context = await CreateTestRegistryContext();
        _appContext = await CreateTestApplicationContext();

        // Mock UserManager<User>
        var store = new Mock<IUserStore<User>>();
        _userManagerMock = new Mock<UserManager<User>>(
            store.Object, null, null, null, null, null, null, null, null);

        _ddbManagerMock = new Mock<IDdbManager>();
        _cacheManagerMock = new Mock<ICacheManager>();

        _datasetLogger = CreateTestLogger<DatasetAccessControl>();
        _orgLogger = CreateTestLogger<OrganizationAccessControl>();

        await SetupUsers();
        await SetupOrganizations();
        await SetupDatasets();
        SetupUserManagerMock();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        await _appContext.DisposeAsync();
    }

    #region Setup Helpers

    private async Task SetupUsers()
    {
        _ownerUser = new User { Id = "owner-id", UserName = "owner", Email = "owner@test.com" };
        _nonMemberUser = new User { Id = "nonmember-id", UserName = "nonmember", Email = "nonmember@test.com" };
        _readOnlyMember = new User { Id = "readonly-id", UserName = "readonly", Email = "readonly@test.com" };
        _readWriteMember = new User { Id = "readwrite-id", UserName = "readwrite", Email = "readwrite@test.com" };
        _readWriteDeleteMember = new User { Id = "rwdelete-id", UserName = "rwdelete", Email = "rwdelete@test.com" };
        _adminMember = new User { Id = "adminmember-id", UserName = "adminmember", Email = "adminmember@test.com" };
        _systemAdminUser = new User { Id = "sysadmin-id", UserName = "sysadmin", Email = "sysadmin@test.com" };
        _deactivatedMember = new User
            { Id = "deactivated-id", UserName = "deactivated", Email = "deactivated@test.com" };
        _deactivatedOwner = new User
            { Id = "deactivated-owner-id", UserName = "deactivatedowner", Email = "deactivatedowner@test.com" };

        _appContext.Users.AddRange(
            _ownerUser, _nonMemberUser, _readOnlyMember, _readWriteMember,
            _readWriteDeleteMember, _adminMember, _systemAdminUser, _deactivatedMember, _deactivatedOwner);
        await _appContext.SaveChangesAsync();
    }

    private async Task SetupOrganizations()
    {
        _privateOrg = new Organization
        {
            Slug = "private-org",
            Name = "Private Organization",
            OwnerId = _ownerUser.Id,
            CreationDate = DateTime.UtcNow,
            IsPublic = false,
            Users = new List<OrganizationUser>()
        };

        _publicOrg = new Organization
        {
            Slug = "public-org",
            Name = "Public Organization",
            OwnerId = _ownerUser.Id,
            CreationDate = DateTime.UtcNow,
            IsPublic = true,
            Users = new List<OrganizationUser>()
        };

        _deactivatedOwnerOrg = new Organization
        {
            Slug = "deactivated-owner-org",
            Name = "Deactivated Owner Org",
            OwnerId = _deactivatedOwner.Id,
            CreationDate = DateTime.UtcNow,
            IsPublic = true,
            Users = new List<OrganizationUser>()
        };

        _context.Organizations.AddRange(_privateOrg, _publicOrg, _deactivatedOwnerOrg);
        await _context.SaveChangesAsync();

        // Add members with different permission levels to BOTH orgs
        var memberships = new[]
        {
            // Private org members
            new OrganizationUser
            {
                OrganizationSlug = _privateOrg.Slug, UserId = _readOnlyMember.Id,
                Permissions = OrganizationPermissions.ReadOnly, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _privateOrg.Slug, UserId = _readWriteMember.Id,
                Permissions = OrganizationPermissions.ReadWrite, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _privateOrg.Slug, UserId = _readWriteDeleteMember.Id,
                Permissions = OrganizationPermissions.ReadWriteDelete, GrantedAt = DateTime.UtcNow,
                GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _privateOrg.Slug, UserId = _adminMember.Id,
                Permissions = OrganizationPermissions.Admin, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _privateOrg.Slug, UserId = _deactivatedMember.Id,
                Permissions = OrganizationPermissions.ReadWrite, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
            // Public org members
            new OrganizationUser
            {
                OrganizationSlug = _publicOrg.Slug, UserId = _readOnlyMember.Id,
                Permissions = OrganizationPermissions.ReadOnly, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _publicOrg.Slug, UserId = _readWriteMember.Id,
                Permissions = OrganizationPermissions.ReadWrite, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _publicOrg.Slug, UserId = _readWriteDeleteMember.Id,
                Permissions = OrganizationPermissions.ReadWriteDelete, GrantedAt = DateTime.UtcNow,
                GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _publicOrg.Slug, UserId = _adminMember.Id,
                Permissions = OrganizationPermissions.Admin, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
            new OrganizationUser
            {
                OrganizationSlug = _publicOrg.Slug, UserId = _deactivatedMember.Id,
                Permissions = OrganizationPermissions.ReadWrite, GrantedAt = DateTime.UtcNow, GrantedBy = _ownerUser.Id
            },
        };

        _context.Set<OrganizationUser>().AddRange(memberships);
        await _context.SaveChangesAsync();
    }

    private async Task SetupDatasets()
    {
        // Datasets for private org
        _privateOrgPrivateDs = new Dataset
        {
            Slug = "private-ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow,
            Organization = _privateOrg
        };
        _privateOrgUnlistedDs = new Dataset
        {
            Slug = "unlisted-ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow,
            Organization = _privateOrg
        };
        _privateOrgPublicDs = new Dataset
        {
            Slug = "public-ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = _privateOrg
        };

        // Datasets for public org
        _publicOrgPrivateDs = new Dataset
        {
            Slug = "private-ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = _publicOrg
        };
        _publicOrgUnlistedDs = new Dataset
        {
            Slug = "unlisted-ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow,
            Organization = _publicOrg
        };
        _publicOrgPublicDs = new Dataset
        {
            Slug = "public-ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = _publicOrg
        };

        // Dataset for deactivated-owner org
        _deactivatedOwnerOrgPublicDs = new Dataset
        {
            Slug = "public-ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow,
            Organization = _deactivatedOwnerOrg
        };

        _context.Datasets.AddRange(
            _privateOrgPrivateDs, _privateOrgUnlistedDs, _privateOrgPublicDs,
            _publicOrgPrivateDs, _publicOrgUnlistedDs, _publicOrgPublicDs,
            _deactivatedOwnerOrgPublicDs);
        await _context.SaveChangesAsync();

        // Setup cache mock to return correct visibility for each dataset
        SetupVisibilityCache(_privateOrgPrivateDs, Visibility.Private);
        SetupVisibilityCache(_privateOrgUnlistedDs, Visibility.Unlisted);
        SetupVisibilityCache(_privateOrgPublicDs, Visibility.Public);
        SetupVisibilityCache(_publicOrgPrivateDs, Visibility.Private);
        SetupVisibilityCache(_publicOrgUnlistedDs, Visibility.Unlisted);
        SetupVisibilityCache(_publicOrgPublicDs, Visibility.Public);
        SetupVisibilityCache(_deactivatedOwnerOrgPublicDs, Visibility.Public);
    }

    private void SetupVisibilityCache(Dataset dataset, Visibility visibility)
    {
        var visibilityBytes = BitConverter.GetBytes((int)visibility);
        var orgSlug = dataset.Organization.Slug;
        var internalRef = dataset.InternalRef;
        _cacheManagerMock.Setup(x => x.GetAsync(
                MagicStrings.DatasetVisibilityCacheSeed,
                orgSlug,
                It.Is<object[]>(p =>
                    p.Length >= 2 &&
                    p[0].ToString() == orgSlug &&
                    p[1] is Guid &&
                    (Guid)p[1] == internalRef)))
            .ReturnsAsync(visibilityBytes);
    }

    private void SetupUserManagerMock()
    {
        // Default: no one is admin or deactivated
        _userManagerMock.Setup(x => x.IsInRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        // System admin
        _userManagerMock.Setup(x => x.IsInRoleAsync(
                It.Is<User>(u => u.Id == _systemAdminUser.Id),
                ApplicationDbContext.AdminRoleName))
            .ReturnsAsync(true);

        // Deactivated member
        _userManagerMock.Setup(x => x.IsInRoleAsync(
                It.Is<User>(u => u.Id == _deactivatedMember.Id),
                ApplicationDbContext.DeactivatedRoleName))
            .ReturnsAsync(true);

        // Deactivated owner
        _userManagerMock.Setup(x => x.IsInRoleAsync(
                It.Is<User>(u => u.Id == _deactivatedOwner.Id),
                ApplicationDbContext.DeactivatedRoleName))
            .ReturnsAsync(true);

        // FindByIdAsync for owner lookups
        _userManagerMock.Setup(x => x.FindByIdAsync(_ownerUser.Id)).ReturnsAsync(_ownerUser);
        _userManagerMock.Setup(x => x.FindByIdAsync(_nonMemberUser.Id)).ReturnsAsync(_nonMemberUser);
        _userManagerMock.Setup(x => x.FindByIdAsync(_readOnlyMember.Id)).ReturnsAsync(_readOnlyMember);
        _userManagerMock.Setup(x => x.FindByIdAsync(_readWriteMember.Id)).ReturnsAsync(_readWriteMember);
        _userManagerMock.Setup(x => x.FindByIdAsync(_readWriteDeleteMember.Id)).ReturnsAsync(_readWriteDeleteMember);
        _userManagerMock.Setup(x => x.FindByIdAsync(_adminMember.Id)).ReturnsAsync(_adminMember);
        _userManagerMock.Setup(x => x.FindByIdAsync(_systemAdminUser.Id)).ReturnsAsync(_systemAdminUser);
        _userManagerMock.Setup(x => x.FindByIdAsync(_deactivatedMember.Id)).ReturnsAsync(_deactivatedMember);
        _userManagerMock.Setup(x => x.FindByIdAsync(_deactivatedOwner.Id)).ReturnsAsync(_deactivatedOwner);
    }

    private OrganizationAccessControl CreateOrgAccessControl()
    {
        return new OrganizationAccessControl(_userManagerMock.Object, _context, _orgLogger);
    }

    private DatasetAccessControl CreateDatasetAccessControl()
    {
        return new DatasetAccessControl(_userManagerMock.Object, _context, _datasetLogger, _ddbManagerMock.Object,
            _cacheManagerMock.Object);
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

    /// <summary>
    /// Returns the appropriate organization based on the isPublic parameter
    /// </summary>
    private Organization GetOrg(bool isPublic) => isPublic ? _publicOrg : _privateOrg;

    /// <summary>
    /// Returns the appropriate dataset based on organization visibility and dataset visibility
    /// </summary>
    private Dataset GetDataset(bool orgIsPublic, Visibility dsVisibility)
    {
        return (orgIsPublic, dsVisibility) switch
        {
            (false, Visibility.Private) => _privateOrgPrivateDs,
            (false, Visibility.Unlisted) => _privateOrgUnlistedDs,
            (false, Visibility.Public) => _privateOrgPublicDs,
            (true, Visibility.Private) => _publicOrgPrivateDs,
            (true, Visibility.Unlisted) => _publicOrgUnlistedDs,
            (true, Visibility.Public) => _publicOrgPublicDs,
            _ => throw new ArgumentException("Invalid combination")
        };
    }

    #endregion

    // ORGANIZATION ACCESS CONTROL MATRIX

    #region Organization Access Control - Anonymous User

    [Test]
    [TestCase(true, AccessType.Read, true, TestName = "Anonymous_PublicOrg_Read_Allowed")]
    [TestCase(true, AccessType.Write, false, TestName = "Anonymous_PublicOrg_Write_Denied")]
    [TestCase(true, AccessType.Delete, false, TestName = "Anonymous_PublicOrg_Delete_Denied")]
    [TestCase(false, AccessType.Read, false, TestName = "Anonymous_PrivateOrg_Read_Denied")]
    [TestCase(false, AccessType.Write, false, TestName = "Anonymous_PrivateOrg_Write_Denied")]
    [TestCase(false, AccessType.Delete, false, TestName = "Anonymous_PrivateOrg_Delete_Denied")]
    public async Task OrgAccess_AnonymousUser(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, null);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - Non-Member

    [Test]
    [TestCase(true, AccessType.Read, false, TestName = "NonMember_PublicOrg_Read_Denied")]
    [TestCase(true, AccessType.Write, false, TestName = "NonMember_PublicOrg_Write_Denied")]
    [TestCase(true, AccessType.Delete, false, TestName = "NonMember_PublicOrg_Delete_Denied")]
    [TestCase(false, AccessType.Read, false, TestName = "NonMember_PrivateOrg_Read_Denied")]
    [TestCase(false, AccessType.Write, false, TestName = "NonMember_PrivateOrg_Write_Denied")]
    [TestCase(false, AccessType.Delete, false, TestName = "NonMember_PrivateOrg_Delete_Denied")]
    public async Task OrgAccess_NonMember(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _nonMemberUser);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - ReadOnly Member

    [Test]
    [TestCase(true, AccessType.Read, true, TestName = "ReadOnly_PublicOrg_Read_Allowed")]
    [TestCase(true, AccessType.Write, false, TestName = "ReadOnly_PublicOrg_Write_Denied")]
    [TestCase(true, AccessType.Delete, false, TestName = "ReadOnly_PublicOrg_Delete_Denied")]
    [TestCase(false, AccessType.Read, true, TestName = "ReadOnly_PrivateOrg_Read_Allowed")]
    [TestCase(false, AccessType.Write, false, TestName = "ReadOnly_PrivateOrg_Write_Denied")]
    [TestCase(false, AccessType.Delete, false, TestName = "ReadOnly_PrivateOrg_Delete_Denied")]
    public async Task OrgAccess_ReadOnlyMember(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _readOnlyMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - ReadWrite Member

    [Test]
    [TestCase(true, AccessType.Read, true, TestName = "ReadWrite_PublicOrg_Read_Allowed")]
    [TestCase(true, AccessType.Write, true, TestName = "ReadWrite_PublicOrg_Write_Allowed")]
    [TestCase(true, AccessType.Delete, false, TestName = "ReadWrite_PublicOrg_Delete_Denied")]
    [TestCase(false, AccessType.Read, true, TestName = "ReadWrite_PrivateOrg_Read_Allowed")]
    [TestCase(false, AccessType.Write, true, TestName = "ReadWrite_PrivateOrg_Write_Allowed")]
    [TestCase(false, AccessType.Delete, false, TestName = "ReadWrite_PrivateOrg_Delete_Denied")]
    public async Task OrgAccess_ReadWriteMember(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _readWriteMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - ReadWriteDelete Member

    [Test]
    [TestCase(true, AccessType.Read, true, TestName = "ReadWriteDelete_PublicOrg_Read_Allowed")]
    [TestCase(true, AccessType.Write, true, TestName = "ReadWriteDelete_PublicOrg_Write_Allowed")]
    [TestCase(true, AccessType.Delete, true, TestName = "ReadWriteDelete_PublicOrg_Delete_Allowed")]
    [TestCase(false, AccessType.Read, true, TestName = "ReadWriteDelete_PrivateOrg_Read_Allowed")]
    [TestCase(false, AccessType.Write, true, TestName = "ReadWriteDelete_PrivateOrg_Write_Allowed")]
    [TestCase(false, AccessType.Delete, true, TestName = "ReadWriteDelete_PrivateOrg_Delete_Allowed")]
    public async Task OrgAccess_ReadWriteDeleteMember(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _readWriteDeleteMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - Admin Member

    [Test]
    [TestCase(true, AccessType.Read, true, TestName = "AdminMember_PublicOrg_Read_Allowed")]
    [TestCase(true, AccessType.Write, true, TestName = "AdminMember_PublicOrg_Write_Allowed")]
    [TestCase(true, AccessType.Delete, true, TestName = "AdminMember_PublicOrg_Delete_Allowed")]
    [TestCase(false, AccessType.Read, true, TestName = "AdminMember_PrivateOrg_Read_Allowed")]
    [TestCase(false, AccessType.Write, true, TestName = "AdminMember_PrivateOrg_Write_Allowed")]
    [TestCase(false, AccessType.Delete, true, TestName = "AdminMember_PrivateOrg_Delete_Allowed")]
    public async Task OrgAccess_AdminMember(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _adminMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - Owner

    [Test]
    [TestCase(true, AccessType.Read, true, TestName = "Owner_PublicOrg_Read_Allowed")]
    [TestCase(true, AccessType.Write, true, TestName = "Owner_PublicOrg_Write_Allowed")]
    [TestCase(true, AccessType.Delete, true, TestName = "Owner_PublicOrg_Delete_Allowed")]
    [TestCase(false, AccessType.Read, true, TestName = "Owner_PrivateOrg_Read_Allowed")]
    [TestCase(false, AccessType.Write, true, TestName = "Owner_PrivateOrg_Write_Allowed")]
    [TestCase(false, AccessType.Delete, true, TestName = "Owner_PrivateOrg_Delete_Allowed")]
    public async Task OrgAccess_Owner(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _ownerUser);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - System Admin

    [Test]
    [TestCase(true, AccessType.Read, true, TestName = "SystemAdmin_PublicOrg_Read_Allowed")]
    [TestCase(true, AccessType.Write, true, TestName = "SystemAdmin_PublicOrg_Write_Allowed")]
    [TestCase(true, AccessType.Delete, true, TestName = "SystemAdmin_PublicOrg_Delete_Allowed")]
    [TestCase(false, AccessType.Read, true, TestName = "SystemAdmin_PrivateOrg_Read_Allowed")]
    [TestCase(false, AccessType.Write, true, TestName = "SystemAdmin_PrivateOrg_Write_Allowed")]
    [TestCase(false, AccessType.Delete, true, TestName = "SystemAdmin_PrivateOrg_Delete_Allowed")]
    public async Task OrgAccess_SystemAdmin(bool orgIsPublic, AccessType access, bool expected)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _systemAdminUser);

        result.ShouldBe(expected);
    }

    #endregion

    #region Organization Access Control - Deactivated User

    [Test]
    [TestCase(true, AccessType.Read, TestName = "DeactivatedMember_PublicOrg_Read_Denied")]
    [TestCase(true, AccessType.Write, TestName = "DeactivatedMember_PublicOrg_Write_Denied")]
    [TestCase(true, AccessType.Delete, TestName = "DeactivatedMember_PublicOrg_Delete_Denied")]
    [TestCase(false, AccessType.Read, TestName = "DeactivatedMember_PrivateOrg_Read_Denied")]
    [TestCase(false, AccessType.Write, TestName = "DeactivatedMember_PrivateOrg_Write_Denied")]
    [TestCase(false, AccessType.Delete, TestName = "DeactivatedMember_PrivateOrg_Delete_Denied")]
    public async Task OrgAccess_DeactivatedMember_AlwaysDenied(bool orgIsPublic, AccessType access)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanAccessOrganization(org, access, _deactivatedMember);

        result.ShouldBeFalse();
    }

    #endregion

    // DATASET ACCESS CONTROL MATRIX

    #region Dataset Access - Anonymous User

    [Test]
    // Private org
    [TestCase(false, Visibility.Private, AccessType.Read, false, TestName = "DsAccess_Anon_PrivateOrg_PrivateDs_Read_Denied")]
    [TestCase(false, Visibility.Private, AccessType.Write, false, TestName = "DsAccess_Anon_PrivateOrg_PrivateDs_Write_Denied")]
    [TestCase(false, Visibility.Private, AccessType.Delete, false, TestName = "DsAccess_Anon_PrivateOrg_PrivateDs_Delete_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true, TestName = "DsAccess_Anon_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, false, TestName = "DsAccess_Anon_PrivateOrg_UnlistedDs_Write_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, false, TestName = "DsAccess_Anon_PrivateOrg_UnlistedDs_Delete_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Read, true, TestName = "DsAccess_Anon_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, false, TestName = "DsAccess_Anon_PrivateOrg_PublicDs_Write_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Delete, false, TestName = "DsAccess_Anon_PrivateOrg_PublicDs_Delete_Denied")]
    // Public org
    [TestCase(true, Visibility.Private, AccessType.Read, false, TestName = "DsAccess_Anon_PublicOrg_PrivateDs_Read_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Write, false, TestName = "DsAccess_Anon_PublicOrg_PrivateDs_Write_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Delete, false, TestName = "DsAccess_Anon_PublicOrg_PrivateDs_Delete_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true, TestName = "DsAccess_Anon_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, false, TestName = "DsAccess_Anon_PublicOrg_UnlistedDs_Write_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, false, TestName = "DsAccess_Anon_PublicOrg_UnlistedDs_Delete_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Read, true, TestName = "DsAccess_Anon_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, false, TestName = "DsAccess_Anon_PublicOrg_PublicDs_Write_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Delete, false, TestName = "DsAccess_Anon_PublicOrg_PublicDs_Delete_Denied")]
    public async Task DsAccess_AnonymousUser(bool orgIsPublic, Visibility dsVisibility, AccessType access,
        bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, null);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - Non-Member

    [Test]
    // Private org
    [TestCase(false, Visibility.Private, AccessType.Read, false,
        TestName = "DsAccess_NonMem_PrivateOrg_PrivateDs_Read_Denied")]
    [TestCase(false, Visibility.Private, AccessType.Write, false,
        TestName = "DsAccess_NonMem_PrivateOrg_PrivateDs_Write_Denied")]
    [TestCase(false, Visibility.Private, AccessType.Delete, false,
        TestName = "DsAccess_NonMem_PrivateOrg_PrivateDs_Delete_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_NonMem_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, false,
        TestName = "DsAccess_NonMem_PrivateOrg_UnlistedDs_Write_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, false,
        TestName = "DsAccess_NonMem_PrivateOrg_UnlistedDs_Delete_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_NonMem_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, false,
        TestName = "DsAccess_NonMem_PrivateOrg_PublicDs_Write_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Delete, false,
        TestName = "DsAccess_NonMem_PrivateOrg_PublicDs_Delete_Denied")]
    // Public org
    [TestCase(true, Visibility.Private, AccessType.Read, false,
        TestName = "DsAccess_NonMem_PublicOrg_PrivateDs_Read_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Write, false,
        TestName = "DsAccess_NonMem_PublicOrg_PrivateDs_Write_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Delete, false,
        TestName = "DsAccess_NonMem_PublicOrg_PrivateDs_Delete_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_NonMem_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, false,
        TestName = "DsAccess_NonMem_PublicOrg_UnlistedDs_Write_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, false,
        TestName = "DsAccess_NonMem_PublicOrg_UnlistedDs_Delete_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_NonMem_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, false,
        TestName = "DsAccess_NonMem_PublicOrg_PublicDs_Write_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Delete, false,
        TestName = "DsAccess_NonMem_PublicOrg_PublicDs_Delete_Denied")]
    public async Task DsAccess_NonMember(bool orgIsPublic, Visibility dsVisibility, AccessType access, bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _nonMemberUser);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - ReadOnly Member

    [Test]
    // Private org
    [TestCase(false, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_ReadOnly_PrivateOrg_PrivateDs_Read_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Write, false,
        TestName = "DsAccess_ReadOnly_PrivateOrg_PrivateDs_Write_Denied")]
    [TestCase(false, Visibility.Private, AccessType.Delete, false,
        TestName = "DsAccess_ReadOnly_PrivateOrg_PrivateDs_Delete_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_ReadOnly_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, false,
        TestName = "DsAccess_ReadOnly_PrivateOrg_UnlistedDs_Write_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, false,
        TestName = "DsAccess_ReadOnly_PrivateOrg_UnlistedDs_Delete_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_ReadOnly_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, false,
        TestName = "DsAccess_ReadOnly_PrivateOrg_PublicDs_Write_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Delete, false,
        TestName = "DsAccess_ReadOnly_PrivateOrg_PublicDs_Delete_Denied")]
    // Public org
    [TestCase(true, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_ReadOnly_PublicOrg_PrivateDs_Read_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Write, false,
        TestName = "DsAccess_ReadOnly_PublicOrg_PrivateDs_Write_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Delete, false,
        TestName = "DsAccess_ReadOnly_PublicOrg_PrivateDs_Delete_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_ReadOnly_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, false,
        TestName = "DsAccess_ReadOnly_PublicOrg_UnlistedDs_Write_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, false,
        TestName = "DsAccess_ReadOnly_PublicOrg_UnlistedDs_Delete_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_ReadOnly_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, false,
        TestName = "DsAccess_ReadOnly_PublicOrg_PublicDs_Write_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Delete, false,
        TestName = "DsAccess_ReadOnly_PublicOrg_PublicDs_Delete_Denied")]
    public async Task DsAccess_ReadOnlyMember(bool orgIsPublic, Visibility dsVisibility, AccessType access,
        bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _readOnlyMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - ReadWrite Member

    [Test]
    // Private org
    [TestCase(false, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_ReadWrite_PrivateOrg_PrivateDs_Read_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_ReadWrite_PrivateOrg_PrivateDs_Write_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Delete, false,
        TestName = "DsAccess_ReadWrite_PrivateOrg_PrivateDs_Delete_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_ReadWrite_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_ReadWrite_PrivateOrg_UnlistedDs_Write_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, false,
        TestName = "DsAccess_ReadWrite_PrivateOrg_UnlistedDs_Delete_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_ReadWrite_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_ReadWrite_PrivateOrg_PublicDs_Write_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Delete, false,
        TestName = "DsAccess_ReadWrite_PrivateOrg_PublicDs_Delete_Denied")]
    // Public org
    [TestCase(true, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_ReadWrite_PublicOrg_PrivateDs_Read_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_ReadWrite_PublicOrg_PrivateDs_Write_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Delete, false,
        TestName = "DsAccess_ReadWrite_PublicOrg_PrivateDs_Delete_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_ReadWrite_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_ReadWrite_PublicOrg_UnlistedDs_Write_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, false,
        TestName = "DsAccess_ReadWrite_PublicOrg_UnlistedDs_Delete_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_ReadWrite_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_ReadWrite_PublicOrg_PublicDs_Write_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Delete, false,
        TestName = "DsAccess_ReadWrite_PublicOrg_PublicDs_Delete_Denied")]
    public async Task DsAccess_ReadWriteMember(bool orgIsPublic, Visibility dsVisibility, AccessType access,
        bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _readWriteMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - ReadWriteDelete Member

    [Test]
    // Private org
    [TestCase(false, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_RWDel_PrivateOrg_PrivateDs_Read_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_RWDel_PrivateOrg_PrivateDs_Write_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_RWDel_PrivateOrg_PrivateDs_Delete_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_RWDel_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_RWDel_PrivateOrg_UnlistedDs_Write_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_RWDel_PrivateOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_RWDel_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_RWDel_PrivateOrg_PublicDs_Write_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_RWDel_PrivateOrg_PublicDs_Delete_Allowed")]
    // Public org
    [TestCase(true, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_RWDel_PublicOrg_PrivateDs_Read_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_RWDel_PublicOrg_PrivateDs_Write_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_RWDel_PublicOrg_PrivateDs_Delete_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_RWDel_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_RWDel_PublicOrg_UnlistedDs_Write_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_RWDel_PublicOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_RWDel_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_RWDel_PublicOrg_PublicDs_Write_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_RWDel_PublicOrg_PublicDs_Delete_Allowed")]
    public async Task DsAccess_ReadWriteDeleteMember(bool orgIsPublic, Visibility dsVisibility, AccessType access,
        bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _readWriteDeleteMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - Admin Member

    [Test]
    // Admin members have full access (permission level >= ReadWriteDelete for all access types,
    // and >= Admin which is even higher)
    [TestCase(false, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_Admin_PrivateOrg_PrivateDs_Read_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_Admin_PrivateOrg_PrivateDs_Write_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_Admin_PrivateOrg_PrivateDs_Delete_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_Admin_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_Admin_PrivateOrg_UnlistedDs_Write_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_Admin_PrivateOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_Admin_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_Admin_PrivateOrg_PublicDs_Write_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_Admin_PrivateOrg_PublicDs_Delete_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_Admin_PublicOrg_PrivateDs_Read_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_Admin_PublicOrg_PrivateDs_Write_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_Admin_PublicOrg_PrivateDs_Delete_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_Admin_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_Admin_PublicOrg_UnlistedDs_Write_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_Admin_PublicOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_Admin_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_Admin_PublicOrg_PublicDs_Write_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_Admin_PublicOrg_PublicDs_Delete_Allowed")]
    public async Task DsAccess_AdminMember(bool orgIsPublic, Visibility dsVisibility, AccessType access, bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _adminMember);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - Owner

    [Test]
    // Owner always has full access regardless of dataset or org visibility
    [TestCase(false, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_Owner_PrivateOrg_PrivateDs_Read_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_Owner_PrivateOrg_PrivateDs_Write_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_Owner_PrivateOrg_PrivateDs_Delete_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_Owner_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_Owner_PrivateOrg_UnlistedDs_Write_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_Owner_PrivateOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_Owner_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_Owner_PrivateOrg_PublicDs_Write_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_Owner_PrivateOrg_PublicDs_Delete_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_Owner_PublicOrg_PrivateDs_Read_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_Owner_PublicOrg_PrivateDs_Write_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_Owner_PublicOrg_PrivateDs_Delete_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_Owner_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_Owner_PublicOrg_UnlistedDs_Write_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_Owner_PublicOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_Owner_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_Owner_PublicOrg_PublicDs_Write_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_Owner_PublicOrg_PublicDs_Delete_Allowed")]
    public async Task DsAccess_Owner(bool orgIsPublic, Visibility dsVisibility, AccessType access, bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _ownerUser);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - System Admin

    [Test]
    // System admin always has full access
    [TestCase(false, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_PrivateDs_Read_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_PrivateDs_Write_Allowed")]
    [TestCase(false, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_PrivateDs_Delete_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_UnlistedDs_Read_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_UnlistedDs_Write_Allowed")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_PublicDs_Read_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_PublicDs_Write_Allowed")]
    [TestCase(false, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_SysAdmin_PrivateOrg_PublicDs_Delete_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Read, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_PrivateDs_Read_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Write, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_PrivateDs_Write_Allowed")]
    [TestCase(true, Visibility.Private, AccessType.Delete, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_PrivateDs_Delete_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_UnlistedDs_Read_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_UnlistedDs_Write_Allowed")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_UnlistedDs_Delete_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Read, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_PublicDs_Read_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Write, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_PublicDs_Write_Allowed")]
    [TestCase(true, Visibility.Public, AccessType.Delete, true,
        TestName = "DsAccess_SysAdmin_PublicOrg_PublicDs_Delete_Allowed")]
    public async Task DsAccess_SystemAdmin(bool orgIsPublic, Visibility dsVisibility, AccessType access, bool expected)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _systemAdminUser);

        result.ShouldBe(expected);
    }

    #endregion

    #region Dataset Access - Deactivated Member

    [Test]
    // Deactivated members should always be denied, regardless of their permission level
    [TestCase(false, Visibility.Private, AccessType.Read,
        TestName = "DsAccess_Deactivated_PrivateOrg_PrivateDs_Read_Denied")]
    [TestCase(false, Visibility.Private, AccessType.Write,
        TestName = "DsAccess_Deactivated_PrivateOrg_PrivateDs_Write_Denied")]
    [TestCase(false, Visibility.Private, AccessType.Delete,
        TestName = "DsAccess_Deactivated_PrivateOrg_PrivateDs_Delete_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Read,
        TestName = "DsAccess_Deactivated_PrivateOrg_UnlistedDs_Read_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Write,
        TestName = "DsAccess_Deactivated_PrivateOrg_UnlistedDs_Write_Denied")]
    [TestCase(false, Visibility.Unlisted, AccessType.Delete,
        TestName = "DsAccess_Deactivated_PrivateOrg_UnlistedDs_Delete_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Read,
        TestName = "DsAccess_Deactivated_PrivateOrg_PublicDs_Read_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Write,
        TestName = "DsAccess_Deactivated_PrivateOrg_PublicDs_Write_Denied")]
    [TestCase(false, Visibility.Public, AccessType.Delete,
        TestName = "DsAccess_Deactivated_PrivateOrg_PublicDs_Delete_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Read,
        TestName = "DsAccess_Deactivated_PublicOrg_PrivateDs_Read_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Write,
        TestName = "DsAccess_Deactivated_PublicOrg_PrivateDs_Write_Denied")]
    [TestCase(true, Visibility.Private, AccessType.Delete,
        TestName = "DsAccess_Deactivated_PublicOrg_PrivateDs_Delete_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Read,
        TestName = "DsAccess_Deactivated_PublicOrg_UnlistedDs_Read_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Write,
        TestName = "DsAccess_Deactivated_PublicOrg_UnlistedDs_Write_Denied")]
    [TestCase(true, Visibility.Unlisted, AccessType.Delete,
        TestName = "DsAccess_Deactivated_PublicOrg_UnlistedDs_Delete_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Read,
        TestName = "DsAccess_Deactivated_PublicOrg_PublicDs_Read_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Write,
        TestName = "DsAccess_Deactivated_PublicOrg_PublicDs_Write_Denied")]
    [TestCase(true, Visibility.Public, AccessType.Delete,
        TestName = "DsAccess_Deactivated_PublicOrg_PublicDs_Delete_Denied")]
    public async Task DsAccess_DeactivatedMember_AlwaysDenied(bool orgIsPublic, Visibility dsVisibility,
        AccessType access)
    {
        var control = CreateDatasetAccessControl();
        var dataset = GetDataset(orgIsPublic, dsVisibility);

        var result = await control.CanAccessDataset(dataset, access, _deactivatedMember);

        result.ShouldBeFalse();
    }

    #endregion

    // MEMBER MANAGEMENT PERMISSIONS

    #region CanManageMembers Tests

    [Test]
    public async Task CanManageMembers_AnonymousUser_Denied()
    {
        var control = CreateOrgAccessControl();

        var result = await control.CanManageMembers(_privateOrg, null);

        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_NonMember_PublicOrg_Denied")]
    [TestCase(false, TestName = "CanManageMembers_NonMember_PrivateOrg_Denied")]
    public async Task CanManageMembers_NonMember_Denied(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _nonMemberUser);

        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_ReadOnly_PublicOrg_Denied")]
    [TestCase(false, TestName = "CanManageMembers_ReadOnly_PrivateOrg_Denied")]
    public async Task CanManageMembers_ReadOnlyMember_Denied(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _readOnlyMember);

        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_ReadWrite_PublicOrg_Denied")]
    [TestCase(false, TestName = "CanManageMembers_ReadWrite_PrivateOrg_Denied")]
    public async Task CanManageMembers_ReadWriteMember_Denied(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _readWriteMember);

        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_ReadWriteDelete_PublicOrg_Denied")]
    [TestCase(false, TestName = "CanManageMembers_ReadWriteDelete_PrivateOrg_Denied")]
    public async Task CanManageMembers_ReadWriteDeleteMember_Denied(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _readWriteDeleteMember);

        result.ShouldBeFalse();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_AdminMember_PublicOrg_Allowed")]
    [TestCase(false, TestName = "CanManageMembers_AdminMember_PrivateOrg_Allowed")]
    public async Task CanManageMembers_AdminMember_Allowed(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _adminMember);

        result.ShouldBeTrue();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_Owner_PublicOrg_Allowed")]
    [TestCase(false, TestName = "CanManageMembers_Owner_PrivateOrg_Allowed")]
    public async Task CanManageMembers_Owner_Allowed(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _ownerUser);

        result.ShouldBeTrue();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_SystemAdmin_PublicOrg_Allowed")]
    [TestCase(false, TestName = "CanManageMembers_SystemAdmin_PrivateOrg_Allowed")]
    public async Task CanManageMembers_SystemAdmin_Allowed(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _systemAdminUser);

        result.ShouldBeTrue();
    }

    [Test]
    [TestCase(true, TestName = "CanManageMembers_DeactivatedMember_PublicOrg_Denied")]
    [TestCase(false, TestName = "CanManageMembers_DeactivatedMember_PrivateOrg_Denied")]
    public async Task CanManageMembers_DeactivatedMember_Denied(bool orgIsPublic)
    {
        var control = CreateOrgAccessControl();
        var org = GetOrg(orgIsPublic);

        var result = await control.CanManageMembers(org, _deactivatedMember);

        result.ShouldBeFalse();
    }

    #endregion

    // ORGANIZATIONS MANAGER - MEMBER MANAGEMENT

    #region OrganizationsManager Member Management Integration

    private Mock<IAuthManager> _authManagerMock;
    private Mock<IOptions<AppSettings>> _appSettingsMock;
    private Mock<IDatasetsManager> _datasetManagerMock;
    private Mock<IUtils> _utilsMock;
    private AppSettings _appSettings;

    private OrganizationsManager CreateOrganizationsManager()
    {
        _authManagerMock = new Mock<IAuthManager>();
        _appSettings = new AppSettings { EnableOrganizationMemberManagement = true };
        _appSettingsMock = new Mock<IOptions<AppSettings>>();
        _appSettingsMock.Setup(x => x.Value).Returns(_appSettings);
        _datasetManagerMock = new Mock<IDatasetsManager>();
        _utilsMock = new Mock<IUtils>();
        var logger = CreateTestLogger<OrganizationsManager>();

        _authManagerMock.Setup(x => x.CanListOrganizations(It.IsAny<User>())).ReturnsAsync(true);
        _authManagerMock.Setup(x => x.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync(true);

        return new OrganizationsManager(
            _authManagerMock.Object,
            _context,
            _utilsMock.Object,
            _datasetManagerMock.Object,
            _appContext,
            _appSettingsMock.Object,
            logger);
    }

    private void SetupManagerAsUser(User user)
    {
        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(user);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(false);
    }

    private void SetupManagerAsSystemAdmin()
    {
        _authManagerMock.Setup(x => x.GetCurrentUser()).ReturnsAsync(_systemAdminUser);
        _authManagerMock.Setup(x => x.IsUserAdmin()).ReturnsAsync(true);
    }

    [Test]
    public async Task Manager_AddMember_AsReadOnlyMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readOnlyMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.AddMember(_privateOrg.Slug, "someuser", OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_AddMember_AsReadWriteMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readWriteMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.AddMember(_privateOrg.Slug, "someuser", OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_AddMember_AsReadWriteDeleteMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readWriteDeleteMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.AddMember(_privateOrg.Slug, "someuser", OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_AddMember_AsAdminMember_Succeeds()
    {
        var newUser = new User { Id = "brand-new-user-id", UserName = "brandnewuser", Email = "brandnew@test.com" };
        _appContext.Users.Add(newUser);
        await _appContext.SaveChangesAsync();

        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_adminMember);

        await manager.AddMember(_privateOrg.Slug, newUser.UserName, OrganizationPermissions.ReadWrite);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == newUser.Id && u.OrganizationSlug == _privateOrg.Slug);

        orgUser.ShouldNotBeNull();
        orgUser.Permissions.ShouldBe(OrganizationPermissions.ReadWrite);
    }

    [Test]
    public async Task Manager_AddMember_AsOwner_Succeeds()
    {
        var newUser = new User { Id = "owner-adds-user-id", UserName = "owneraddsuser", Email = "owneradds@test.com" };
        _appContext.Users.Add(newUser);
        await _appContext.SaveChangesAsync();

        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        await manager.AddMember(_privateOrg.Slug, newUser.UserName, OrganizationPermissions.ReadOnly);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == newUser.Id && u.OrganizationSlug == _privateOrg.Slug);

        orgUser.ShouldNotBeNull();
        orgUser.Permissions.ShouldBe(OrganizationPermissions.ReadOnly);
    }

    [Test]
    public async Task Manager_AddMember_AsSystemAdmin_Succeeds()
    {
        var newUser = new User { Id = "sysadmin-adds-id", UserName = "sysadminadds", Email = "sysadminadds@test.com" };
        _appContext.Users.Add(newUser);
        await _appContext.SaveChangesAsync();

        var manager = CreateOrganizationsManager();
        SetupManagerAsSystemAdmin();

        await manager.AddMember(_privateOrg.Slug, newUser.UserName, OrganizationPermissions.Admin);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == newUser.Id && u.OrganizationSlug == _privateOrg.Slug);

        orgUser.ShouldNotBeNull();
        orgUser.Permissions.ShouldBe(OrganizationPermissions.Admin);
    }

    [Test]
    public async Task Manager_AddMember_AsNonMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_nonMemberUser);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.AddMember(_privateOrg.Slug, "someuser", OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_UpdatePermission_AsReadOnlyMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readOnlyMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.UpdateMemberPermission(_privateOrg.Slug, _readWriteMember.UserName, OrganizationPermissions.Admin));
    }

    [Test]
    public async Task Manager_UpdatePermission_AsReadWriteMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readWriteMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.UpdateMemberPermission(_privateOrg.Slug, _readOnlyMember.UserName, OrganizationPermissions.Admin));
    }

    [Test]
    public async Task Manager_UpdatePermission_AsReadWriteDeleteMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readWriteDeleteMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.UpdateMemberPermission(_privateOrg.Slug, _readOnlyMember.UserName, OrganizationPermissions.Admin));
    }

    [Test]
    public async Task Manager_UpdatePermission_AsAdminMember_Succeeds()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_adminMember);

        await manager.UpdateMemberPermission(_privateOrg.Slug, _readOnlyMember.UserName,
            OrganizationPermissions.ReadWrite);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == _readOnlyMember.Id && u.OrganizationSlug == _privateOrg.Slug);

        orgUser.ShouldNotBeNull();
        orgUser.Permissions.ShouldBe(OrganizationPermissions.ReadWrite);
    }

    [Test]
    public async Task Manager_RemoveMember_AsReadOnlyMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readOnlyMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.RemoveMember(_privateOrg.Slug, _readWriteMember.UserName));
    }

    [Test]
    public async Task Manager_RemoveMember_AsReadWriteMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readWriteMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.RemoveMember(_privateOrg.Slug, _readOnlyMember.UserName));
    }

    [Test]
    public async Task Manager_RemoveMember_AsReadWriteDeleteMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readWriteDeleteMember);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.RemoveMember(_privateOrg.Slug, _readOnlyMember.UserName));
    }

    [Test]
    public async Task Manager_RemoveMember_AsAdminMember_Succeeds()
    {
        // Add a temporary member to remove
        var tempUser = new User { Id = "temp-remove-id", UserName = "tempremove", Email = "tempremove@test.com" };
        _appContext.Users.Add(tempUser);
        await _appContext.SaveChangesAsync();
        _context.Set<OrganizationUser>().Add(new OrganizationUser
        {
            OrganizationSlug = _privateOrg.Slug,
            UserId = tempUser.Id,
            Permissions = OrganizationPermissions.ReadOnly,
            GrantedAt = DateTime.UtcNow,
            GrantedBy = _ownerUser.Id
        });
        await _context.SaveChangesAsync();

        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_adminMember);

        await manager.RemoveMember(_privateOrg.Slug, tempUser.UserName);

        var orgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == tempUser.Id && u.OrganizationSlug == _privateOrg.Slug);

        orgUser.ShouldBeNull();
    }

    [Test]
    public async Task Manager_GetMembers_AsNonMember_ThrowsUnauthorized()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_nonMemberUser);

        await Should.ThrowAsync<UnauthorizedException>(
            manager.GetMembers(_privateOrg.Slug));
    }

    [Test]
    public async Task Manager_GetMembers_AsReadOnlyMember_ReturnsMembers()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_readOnlyMember);

        var members = (await manager.GetMembers(_privateOrg.Slug)).ToList();

        members.ShouldNotBeEmpty();
    }

    [Test]
    public async Task Manager_GetMembers_AsOwner_ReturnsAllMembers()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        var members = (await manager.GetMembers(_privateOrg.Slug)).ToList();

        members.ShouldNotBeEmpty();
        members.ShouldContain(m => m.UserName == _readOnlyMember.UserName);
        members.ShouldContain(m => m.UserName == _readWriteMember.UserName);
        members.ShouldContain(m => m.UserName == _readWriteDeleteMember.UserName);
        members.ShouldContain(m => m.UserName == _adminMember.UserName);
    }

    [Test]
    public async Task Manager_GetMembers_AsSystemAdmin_ReturnsAllMembers()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsSystemAdmin();

        var members = (await manager.GetMembers(_privateOrg.Slug)).ToList();

        members.ShouldNotBeEmpty();
    }

    #endregion

    // CORNER CASES

    #region Corner Cases

    [Test]
    public async Task DsAccess_AnonymousUser_DeactivatedOwner_PublicDs_Denied()
    {
        // When the org owner is deactivated, anonymous cannot read even public datasets
        var control = CreateDatasetAccessControl();

        var result = await control.CanAccessDataset(_deactivatedOwnerOrgPublicDs, AccessType.Read, null);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task OrgAccess_AnonymousUser_DeactivatedOwner_PublicOrg_Denied()
    {
        // When the org owner is deactivated, anonymous cannot read the public org
        var control = CreateOrgAccessControl();

        var result = await control.CanAccessOrganization(_deactivatedOwnerOrg, AccessType.Read, null);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task DsAccess_RemovedMember_LosesAccess_ToPrivateDataset()
    {
        // A user who was a member but got removed should not have access
        var exMember = new User { Id = "ex-member-id", UserName = "exmember", Email = "exmember@test.com" };
        _appContext.Users.Add(exMember);
        await _appContext.SaveChangesAsync();
        _userManagerMock.Setup(x => x.FindByIdAsync(exMember.Id)).ReturnsAsync(exMember);
        _userManagerMock.Setup(x => x.IsInRoleAsync(exMember, It.IsAny<string>())).ReturnsAsync(false);

        // Add then remove member
        var orgUser = new OrganizationUser
        {
            OrganizationSlug = _privateOrg.Slug,
            UserId = exMember.Id,
            Permissions = OrganizationPermissions.ReadWrite,
            GrantedAt = DateTime.UtcNow,
            GrantedBy = _ownerUser.Id
        };
        _context.Set<OrganizationUser>().Add(orgUser);
        await _context.SaveChangesAsync();

        // Verify access while member
        var control = CreateDatasetAccessControl();
        var resultBefore = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Read, exMember);
        resultBefore.ShouldBeTrue();

        // Remove member
        _context.Set<OrganizationUser>().Remove(orgUser);
        await _context.SaveChangesAsync();

        // Reload org to clear cached Users collection
        await _context.Entry(_privateOrg).Collection(o => o.Users).LoadAsync();

        // Verify access lost
        var resultAfter = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Read, exMember);
        resultAfter.ShouldBeFalse();
    }

    [Test]
    public async Task DsAccess_PrivateDataset_InPublicOrg_AnonymousDenied()
    {
        // Even though the org is public, anonymous cannot read a private dataset
        var control = CreateDatasetAccessControl();

        var result = await control.CanAccessDataset(_publicOrgPrivateDs, AccessType.Read, null);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task DsAccess_PublicDataset_InPrivateOrg_AnonymousCanRead()
    {
        // Anonymous users can read a public dataset even if the org is private
        // (DatasetAccessControl checks dataset visibility, not org visibility)
        var control = CreateDatasetAccessControl();

        var result = await control.CanAccessDataset(_privateOrgPublicDs, AccessType.Read, null);

        result.ShouldBeTrue();
    }

    [Test]
    public async Task DsAccess_OwnerNotMember_StillHasFullAccess()
    {
        // Owner is not in the OrganizationUser table but should still have full access
        var ownerOrgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == _ownerUser.Id && u.OrganizationSlug == _privateOrg.Slug);

        // Confirm owner is NOT a member (just an owner)
        ownerOrgUser.ShouldBeNull();

        var control = CreateDatasetAccessControl();

        var canRead = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Read, _ownerUser);
        var canWrite = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Write, _ownerUser);
        var canDelete = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Delete, _ownerUser);

        canRead.ShouldBeTrue();
        canWrite.ShouldBeTrue();
        canDelete.ShouldBeTrue();
    }

    [Test]
    public async Task DsAccess_SystemAdmin_NotMember_StillHasFullAccess()
    {
        // System admin is not an organization member but should have full access
        var sysAdminOrgUser = await _context.Set<OrganizationUser>()
            .FirstOrDefaultAsync(u => u.UserId == _systemAdminUser.Id && u.OrganizationSlug == _privateOrg.Slug);

        // Confirm system admin is NOT a member
        sysAdminOrgUser.ShouldBeNull();

        var control = CreateDatasetAccessControl();

        var canRead = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Read, _systemAdminUser);
        var canWrite = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Write, _systemAdminUser);
        var canDelete = await control.CanAccessDataset(_privateOrgPrivateDs, AccessType.Delete, _systemAdminUser);

        canRead.ShouldBeTrue();
        canWrite.ShouldBeTrue();
        canDelete.ShouldBeTrue();
    }

    [Test]
    public async Task OrgAccess_NonMember_PrivateOrg_ReadDenied()
    {
        // Non-member cannot even read a private organization (unlike datasets which check visibility)
        var control = CreateOrgAccessControl();

        var result = await control.CanAccessOrganization(_privateOrg, AccessType.Read, _nonMemberUser);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task DsAccess_NonMember_PrivateDs_WriteDenied_EvenIfPublicOrUnlisted()
    {
        // Non-member can only READ public/unlisted datasets, never write/delete
        var control = CreateDatasetAccessControl();

        var writePublic = await control.CanAccessDataset(_publicOrgPublicDs, AccessType.Write, _nonMemberUser);
        var deletePublic = await control.CanAccessDataset(_publicOrgPublicDs, AccessType.Delete, _nonMemberUser);
        var writeUnlisted = await control.CanAccessDataset(_publicOrgUnlistedDs, AccessType.Write, _nonMemberUser);
        var deleteUnlisted = await control.CanAccessDataset(_publicOrgUnlistedDs, AccessType.Delete, _nonMemberUser);

        writePublic.ShouldBeFalse();
        deletePublic.ShouldBeFalse();
        writeUnlisted.ShouldBeFalse();
        deleteUnlisted.ShouldBeFalse();
    }

    [Test]
    public async Task Manager_AddMember_FeatureDisabled_AllRolesDenied()
    {
        var manager = CreateOrganizationsManager();
        _appSettings.EnableOrganizationMemberManagement = false;

        // Even owner cannot add members when feature is disabled
        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<InvalidOperationException>(
            manager.AddMember(_privateOrg.Slug, "someuser", OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_RemoveMember_FeatureDisabled_AllRolesDenied()
    {
        var manager = CreateOrganizationsManager();
        _appSettings.EnableOrganizationMemberManagement = false;

        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<InvalidOperationException>(
            manager.RemoveMember(_privateOrg.Slug, _readOnlyMember.UserName));
    }

    [Test]
    public async Task Manager_UpdatePermission_FeatureDisabled_AllRolesDenied()
    {
        var manager = CreateOrganizationsManager();
        _appSettings.EnableOrganizationMemberManagement = false;

        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<InvalidOperationException>(
            manager.UpdateMemberPermission(_privateOrg.Slug, _readOnlyMember.UserName, OrganizationPermissions.Admin));
    }

    [Test]
    public async Task Manager_AddMember_OwnerAsTarget_ThrowsInvalidOperation()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<InvalidOperationException>(
            manager.AddMember(_privateOrg.Slug, _ownerUser.UserName, OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_AddMember_AlreadyMember_ThrowsConflict()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<ConflictException>(
            manager.AddMember(_privateOrg.Slug, _readWriteMember.UserName, OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_AddMember_NonExistentUser_ThrowsNotFound()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<NotFoundException>(
            manager.AddMember(_privateOrg.Slug, "totally-nonexistent-user", OrganizationPermissions.ReadWrite));
    }

    [Test]
    public async Task Manager_UpdatePermission_NonMemberTarget_ThrowsNotFound()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<NotFoundException>(
            manager.UpdateMemberPermission(_privateOrg.Slug, _nonMemberUser.UserName, OrganizationPermissions.Admin));
    }

    [Test]
    public async Task Manager_RemoveMember_NonMemberTarget_ThrowsNotFound()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<NotFoundException>(
            manager.RemoveMember(_privateOrg.Slug, _nonMemberUser.UserName));
    }

    [Test]
    public async Task Manager_GetMembers_NonExistentOrg_ThrowsNotFound()
    {
        var manager = CreateOrganizationsManager();
        SetupManagerAsUser(_ownerUser);

        await Should.ThrowAsync<NotFoundException>(
            manager.GetMembers("completely-nonexistent-org"));
    }

    [Test]
    public async Task GetUserPermission_ReturnsCorrectPermission_ForEachRole()
    {
        var control = CreateOrgAccessControl();

        var readOnlyPerm = await control.GetUserPermission(_privateOrg, _readOnlyMember);
        var readWritePerm = await control.GetUserPermission(_privateOrg, _readWriteMember);
        var rwDeletePerm = await control.GetUserPermission(_privateOrg, _readWriteDeleteMember);
        var adminPerm = await control.GetUserPermission(_privateOrg, _adminMember);
        var nonMemberPerm = await control.GetUserPermission(_privateOrg, _nonMemberUser);
        var nullUserPerm = await control.GetUserPermission(_privateOrg, null);

        readOnlyPerm.ShouldBe(OrganizationPermissions.ReadOnly);
        readWritePerm.ShouldBe(OrganizationPermissions.ReadWrite);
        rwDeletePerm.ShouldBe(OrganizationPermissions.ReadWriteDelete);
        adminPerm.ShouldBe(OrganizationPermissions.Admin);
        nonMemberPerm.ShouldBeNull();
        nullUserPerm.ShouldBeNull();
    }

    #endregion
}