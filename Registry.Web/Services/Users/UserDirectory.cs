using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>The users as administrators list them: with their roles, organizations and storage, and their memberships.</summary>
public sealed class UserDirectory
{
    private readonly UserManager<User> _userManager;
    private readonly IAuthManager _authManager;
    private readonly IStorageQuota _quota;
    private readonly OrganizationMembershipService _memberships;
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly RegistryContext _registryContext;

    public UserDirectory(UserManager<User> userManager, IAuthManager authManager, IStorageQuota quota, OrganizationMembershipService memberships, ApplicationDbContext applicationDbContext, RegistryContext registryContext)
    {
        _userManager = userManager;
        _authManager = authManager;
        _quota = quota;
        _memberships = memberships;
        _applicationDbContext = applicationDbContext;
        _registryContext = registryContext;
    }

    public async Task<UserStorageInfo> GetUserStorageInfo(string? userName = null)
    {
        var user = await _authManager.GetCurrentUser();

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (string.IsNullOrWhiteSpace(userName))
            return _quota.GetUserStorage(user);

        user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        return _quota.GetUserStorage(user);
    }

    public async Task<UserOrganizationMembershipDto[]> GetUserOrganizations(string userName,
        CancellationToken ct = default)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new BadRequestException("User does not exist");

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        var orgs = await _registryContext.Organizations
            .Include(o => o.Users)
            .AsNoTracking()
            .Where(OwnedOrJoinedBy(user.Id))
            .ToListAsync(ct);

        var results = new List<UserOrganizationMembershipDto>();

        foreach (var org in orgs)
        {
            var isOwner = org.OwnerId == user.Id;
            var membership = org.Users?.FirstOrDefault(u => u.UserId == user.Id);

            string? grantedByUserName = null;
            if (membership?.GrantedBy != null)
            {
                var grantedByUser = await _applicationDbContext.Users.FindAsync([membership.GrantedBy], ct);
                grantedByUserName = grantedByUser?.UserName;
            }

            results.Add(new UserOrganizationMembershipDto
            {
                Slug = org.Slug,
                Name = org.Name,
                Description = org.Description,
                IsPublic = org.IsPublic,
                IsOwner = isOwner,
                Permissions = isOwner ? null : membership?.Permissions,
                GrantedAt = isOwner ? null : membership?.GrantedAt,
                GrantedBy = isOwner ? null : grantedByUserName
            });
        }

        return [.. results];
    }

    /// <summary>
    /// The signed-in user as they see themselves: their name, e-mail, roles and the slugs of the organizations they own
    /// or belong to (by slug), the same organizations an administrator sees for them.
    /// </summary>
    public async Task<UserDto> GetCurrentUserProfile(CancellationToken ct = default)
    {
        var user = await _authManager.GetRequiredCurrentUser();

        return new UserDto
        {
            UserName = user.UserName,
            Email = user.Email,
            Roles = [.. (await _userManager.GetRolesAsync(user)).Order(StringComparer.Ordinal)],
            Organizations = await _registryContext.Organizations
                .AsNoTracking()
                .Where(OwnedOrJoinedBy(user.Id))
                .Select(org => org.Slug)
                .OrderBy(slug => slug)
                .ToArrayAsync(ct)
        };
    }

    /// <summary>The organizations a user owns or is a member of.</summary>
    private static Expression<Func<Organization, bool>> OwnedOrJoinedBy(string userId) =>
        org => org.OwnerId == userId || org.Users.Any(item => item.UserId == userId);

    // The administrator routes (the Admin policy gates them); the rows are the membership service's
    public async Task AddUserToOrganization(string userName, string orgSlug, OrganizationPermissions permissions)
    {
        var org = await _memberships.GetWithMembersAsync(orgSlug);
        await _memberships.AddAsync(org, userName, permissions, await _authManager.GetRequiredCurrentUser());
    }

    public async Task RemoveUserFromOrganization(string userName, string orgSlug)
    {
        var org = await _memberships.GetWithMembersAsync(orgSlug);
        await _memberships.RemoveAsync(org, userName, await _authManager.GetRequiredCurrentUser());
    }

    public async Task UpdateUserOrganizationPermissions(string userName, string orgSlug,
        OrganizationPermissions permissions)
    {
        var org = await _memberships.GetWithMembersAsync(orgSlug);
        await _memberships.UpdateAsync(org, userName, permissions, await _authManager.GetRequiredCurrentUser());
    }

    public async Task<IEnumerable<UserDto>> GetAll(CancellationToken ct = default)
    {
        var userOrgQuery = from orgusr in _registryContext.OrganizationsUsers
            group orgusr by orgusr.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.OrganizationSlug).ToArray()
            };

        var userOrgQuery2 = from org in _registryContext.Organizations
            where org.OwnerId != null
            group org by org.OwnerId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.Slug).ToArray()
            };

        var union = (await userOrgQuery.ToArrayAsync(ct)).Union(await userOrgQuery2.ToArrayAsync(ct));

        var merge = from m in union
            group m by m.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.SelectMany(item => item.OrgIds).ToArray()
            };

        var userOrg = merge.ToDictionary(item => item.UserId, item => item.OrgIds);

        return (await GetUsersWithRolesAsync(ct)).Select(user => new UserDto
        {
            UserName = user.UserName,
            Email = user.Email,
            Roles = user.Roles,
            Organizations = userOrg.SafeGetValue(user.Id)
        });
    }

    /// <summary>A user and the names of their roles (empty when they have none).</summary>
    private sealed record UserWithRoles(string Id, string UserName, string Email, string[] Roles);

    /// <summary>
    /// Every user with the names of their roles, for the user listings. The LEFT JOIN gives a user
    /// without a role one row with a null role name: that row is the user, not a role, so their
    /// list is empty.
    /// </summary>
    private async Task<UserWithRoles[]> GetUsersWithRolesAsync(CancellationToken ct)
    {
        var rows = await (from user in _applicationDbContext.Users
            join userRole in _applicationDbContext.UserRoles on user.Id equals userRole.UserId into userRoles
            from userRole in userRoles.DefaultIfEmpty()
            join role in _applicationDbContext.Roles on userRole.RoleId equals role.Id into roles
            from role in roles.DefaultIfEmpty()
            select new
            {
                UserId = user.Id,
                user.UserName,
                user.Email,
                RoleName = role.Name
            }).ToArrayAsync(ct);

        return
        [
            .. rows.GroupBy(row => row.UserId).Select(user => new UserWithRoles(
                user.Key, user.First().UserName, user.First().Email,
                [.. user.Select(row => row.RoleName).Where(name => !string.IsNullOrEmpty(name))]))
        ];
    }

    public async Task<IEnumerable<UserDetailDto>> GetAllDetailed(CancellationToken ct = default)
    {
        var userOrgQuery = from orgusr in _registryContext.OrganizationsUsers
            group orgusr by orgusr.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.OrganizationSlug).ToArray(),
                OrgCount = g.Count()
            };

        var userOrgQuery2 = from org in _registryContext.Organizations
            where org.OwnerId != null
            group org by org.OwnerId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.Select(item => item.Slug).ToArray(),
                OrgCount = g.Count()
            };

        var union = (await userOrgQuery.ToArrayAsync(ct)).Union(await userOrgQuery2.ToArrayAsync(ct));

        var merge = from m in union
            group m by m.UserId
            into g
            select new
            {
                UserId = g.Key,
                OrgIds = g.SelectMany(item => item.OrgIds).ToArray(),
                OrgCount = g.Sum(item => item.OrgCount)
            };

        var userOrg = merge.ToDictionary(item => item.UserId,
            item => new { item.OrgIds, item.OrgCount });

        // Dataset count per organization
        var datasetQuery = from ds in _registryContext.Datasets
            group ds by ds.Organization.Slug
            into g
            select new
            {
                OrgSlug = g.Key,
                DatasetCount = g.Count()
            };

        var datasetCounts = await datasetQuery.ToDictionaryAsync(item => item.OrgSlug, item => item.DatasetCount, ct);

        var users = new List<UserDetailDto>();

        foreach (var user in await GetUsersWithRolesAsync(ct))
        {
            var orgInfo = userOrg.SafeGetValue(user.Id);
            var storageInfo = await GetUserStorageInfo(user.UserName);

            // Calculate total datasets for this user across all their organizations
            var totalDatasets = 0;
            if (orgInfo?.OrgIds != null)
            {
                totalDatasets = orgInfo.OrgIds.Sum(orgSlug => datasetCounts.SafeGetValue(orgSlug));
            }

            users.Add(new UserDetailDto
            {
                Id = user.Id,
                UserName = user.UserName,
                Email = user.Email,
                Roles = user.Roles,
                Organizations = orgInfo?.OrgIds ?? [],
                StorageQuota = storageInfo.Total,
                StorageUsed = storageInfo.Used,
                OrganizationCount = orgInfo?.OrgCount ?? 0,
                DatasetCount = totalDatasets,
                CreatedDate = DateTime.UtcNow // Placeholder since User doesn't have Created field
            });
        }

        return users;
    }
}
