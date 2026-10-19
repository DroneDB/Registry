using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Handles organization-specific access control
/// </summary>
public class OrganizationAccessControl : AccessControlBase
{
    public OrganizationAccessControl(IUserAuthSnapshotProvider snapshots, RegistryContext context, ILogger logger)
        : base(snapshots, context, logger)
    {
    }

    public Task<bool> CanAccessOrganization(Organization org, AccessType access, User user)
        => CanAccessOrganization(org, access, user?.Id);

    /// <param name="userId">Id of an existing user, or null for anonymous access</param>
    public async Task<bool> CanAccessOrganization(Organization org, AccessType access, string userId)
    {
        ArgumentNullException.ThrowIfNull(org);

        // Anonymous access check
        if (userId == null)
        {
            // Anonymous users can access only public organizations with Read access,
            // and only while the owner is not deactivated
            if (access != AccessType.Read || !org.IsPublic)
                return false;

            return await IsOwnerActive(org.OwnerId);
        }

        var user = await Snapshots.GetAsync(userId);

        // Deactivated user check
        if (user.IsDeactivated)
            return false;

        // Admin privileges - system admins have full access
        if (user.IsAdmin)
        {
            return true;
        }

        // Owner privileges - owner always has full access (except delete on public org)
        var isOwner = org.OwnerId == userId;
        if (isOwner)
        {
            // Owner can perform any action except delete on the public organization
            return org.Slug != MagicStrings.PublicOrganizationSlug || access != AccessType.Delete;
        }

        // Load organization users if not already loaded
        if (org.Users == null)
            await _context.Entry(org).Collection(o => o.Users).LoadAsync();

        // Find the user's membership
        var orgUser = org.Users?.FirstOrDefault(u => u.UserId == userId);
        if (orgUser == null)
            return false;

        // Check permission level against requested access
        var permission = (OrganizationPermissions)orgUser.Permissions;
        return permission.HasAccess(access);
    }

    /// <summary>
    /// Checks if the user can manage members of the organization
    /// </summary>
    public async Task<bool> CanManageMembers(Organization org, User user)
    {
        ArgumentNullException.ThrowIfNull(org);

        if (user == null)
            return false;

        if (await IsUserDeactivated(user.Id))
            return false;

        // System admin can always manage members
        if (await IsUserAdmin(user.Id))
            return true;

        // Owner can always manage members
        if (org.OwnerId == user.Id)
            return true;

        // Load organization users if not already loaded
        if (org.Users == null)
            await _context.Entry(org).Collection(o => o.Users).LoadAsync();

        // Check if member has Admin permission
        var orgUser = org.Users?.FirstOrDefault(u => u.UserId == user.Id);
        if (orgUser == null)
            return false;

        var permission = (OrganizationPermissions)orgUser.Permissions;
        return permission.CanManageMembers();
    }

    /// <summary>
    /// Gets the permission level for a user in an organization
    /// Returns null if user is not a member
    /// </summary>
    public async Task<OrganizationPermissions?> GetUserPermission(Organization org, User user)
    {
        if (org == null || user == null)
            return null;

        // Load organization users if not already loaded
        if (org.Users == null)
            await _context.Entry(org).Collection(o => o.Users).LoadAsync();

        var orgUser = org.Users?.FirstOrDefault(u => u.UserId == user.Id);
        if (orgUser == null)
            return null;

        return (OrganizationPermissions)orgUser.Permissions;
    }
}