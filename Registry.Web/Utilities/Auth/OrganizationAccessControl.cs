using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Managers;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Handles organization-specific access control
/// </summary>
public class OrganizationAccessControl : AccessControlBase
{
    public OrganizationAccessControl(UserManager<User> usersManager, ILogger logger)
        : base(usersManager, logger) { }

    public async Task<bool> CanAccessOrganization(Organization org, AccessType access, User user)
    {
        ArgumentNullException.ThrowIfNull(org);

        // Anonymous access check
        if (user == null)
        {
            // Anonymous users can access only public organizations with Read access
            if (access != AccessType.Read || !org.IsPublic)
                return false;

            var owner = await UsersManager.FindByIdAsync(org.OwnerId);

            // Check if the owner is not deactivated
            return owner != null && !await IsUserDeactivated(owner);
        }

        // Deactivated user check
        if (await IsUserDeactivated(user))
            return false;

        // Admin privileges
        var isAdmin = await IsUserAdmin(user);
        if (isAdmin)
        {
            // Admin can access any organization and perform any action
            return true;
        }

        // Owner privileges
        var isOwner = org.OwnerId == user.Id;
        if (isOwner)
        {
            // Owner can perform any action except delete on the public organization
            return org.Slug != MagicStrings.PublicOrganizationSlug || access != AccessType.Delete;
        }

        // Regular organization user access
        if (access == AccessType.Delete)
            return false;

        var orgUser = org.Users.FirstOrDefault(u => u.UserId == user.Id);
        return orgUser != null && !await IsUserDeactivated(user);
    }
}
