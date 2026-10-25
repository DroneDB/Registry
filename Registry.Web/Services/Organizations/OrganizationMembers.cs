using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Organizations;

/// <summary>The members of an organization (out of <c>OrganizationsManager</c>).</summary>
public sealed class OrganizationMembers
{
    private readonly IAuthManager _authManager;
    private readonly OrganizationMembershipService _memberships;
    private readonly RegistryContext _context;
    private readonly ApplicationDbContext _appContext;
    private readonly IOptions<AccountOptions> _accounts;

    public OrganizationMembers(IAuthManager authManager, OrganizationMembershipService memberships,
        RegistryContext context, ApplicationDbContext appContext, IOptions<AccountOptions> accounts)
    {
        _authManager = authManager;
        _memberships = memberships;
        _context = context;
        _appContext = appContext;
        _accounts = accounts;
    }

    public bool IsMemberManagementEnabled => _accounts.Value.EnableOrganizationMemberManagement;

    /// <summary>The members of an organization, for an administrator, its owner or one of its members.</summary>
    public async Task<IEnumerable<OrganizationMemberDto>> GetMembers(string orgSlug, CancellationToken ct = default)
    {
        var org = await _context.Organizations
            .Include(o => o.Users)
            .FirstOrDefaultAsync(o => o.Slug == orgSlug, ct);

        if (org == null)
            throw new NotFoundException($"Organization '{orgSlug}' not found");

        // Check if current user can view members
        var currentUser = await _authManager.GetCurrentUser();
        if (!await _authManager.IsUserAdmin() &&
            org.OwnerId != currentUser?.Id)
        {
            // Check if user is a member with sufficient permissions
            var userMembership = org.Users?.FirstOrDefault(u => u.UserId == currentUser?.Id);
            if (userMembership == null)
                throw new ForbiddenException("Access denied");
        }

        var members = new List<OrganizationMemberDto>();

        foreach (var orgUser in org.Users ?? Enumerable.Empty<OrganizationUser>())
        {
            var user = await _appContext.Users.FindAsync([orgUser.UserId], ct);
            if (user == null) continue;

            var grantedByUser = !string.IsNullOrEmpty(orgUser.GrantedBy)
                ? await _appContext.Users.FindAsync([orgUser.GrantedBy], ct)
                : null;

            members.Add(new OrganizationMemberDto
            {
                UserName = user.UserName,
                Email = user.Email,
                Permissions = orgUser.Permissions,
                GrantedAt = orgUser.GrantedAt,
                GrantedBy = grantedByUser?.UserName
            });
        }

        return members;
    }

    // The organization (with its members) whose members the current user is about to change: it must
    // exist, the user must be allowed to manage its members, and the feature must be on. The access
    // check comes before the feature check, so an anonymous caller gets 401 and a stranger 403
    private async Task<(Organization Org, User CurrentUser)> GetOrganizationToManageMembers(string orgSlug,
        CancellationToken ct)
    {
        var org = await _memberships.GetWithMembersAsync(orgSlug, ct);

        var currentUser = await _authManager.GetCurrentUser();
        if (!await CanManageMembers(org, currentUser))
            throw new ForbiddenException("You don't have permission to manage members");

        if (!IsMemberManagementEnabled)
            throw new ForbiddenException("Organization member management is disabled");

        return (org, currentUser);
    }

    /// <summary>Adds a member (whoever may manage the members, while the feature is on).</summary>
    public async Task AddMember(string orgSlug, string userName, OrganizationPermissions permissions = OrganizationPermissions.ReadWrite,
        CancellationToken ct = default)
    {
        var (org, currentUser) = await GetOrganizationToManageMembers(orgSlug, ct);
        await _memberships.AddAsync(org, userName, permissions, currentUser, CancellationToken.None);
    }

    /// <summary>Changes the permissions of a member.</summary>
    public async Task UpdateMemberPermission(string orgSlug, string userName, OrganizationPermissions permission,
        CancellationToken ct = default)
    {
        var (org, currentUser) = await GetOrganizationToManageMembers(orgSlug, ct);
        await _memberships.UpdateAsync(org, userName, permission, currentUser, CancellationToken.None);
    }

    /// <summary>Removes a member.</summary>
    public async Task RemoveMember(string orgSlug, string userName, CancellationToken ct = default)
    {
        var (org, currentUser) = await GetOrganizationToManageMembers(orgSlug, ct);
        await _memberships.RemoveAsync(org, userName, currentUser, CancellationToken.None);
    }

    private async Task<bool> CanManageMembers(Organization org, User user)
    {
        if (user == null) return false;

        // System admin can always manage
        if (await _authManager.IsUserAdmin()) return true;

        // Owner can always manage
        if (org.OwnerId == user.Id) return true;

        // Check if member has Admin permission
        var orgUser = org.Users?.FirstOrDefault(u => u.UserId == user.Id);
        if (orgUser == null) return false;

        return orgUser.Permissions.CanManageMembers();
    }
}
