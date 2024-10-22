using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;

namespace Registry.Web.Services.Managers;

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
            return access == AccessType.Read && org.IsPublic;

        // Deactivated user check
        if (await IsUserDeactivated(user))
            return false;

        // Admin/owner privileges
        var isOwner = org.OwnerId == user.Id;
        var isAdmin = await IsUserAdmin(user);

        if (isOwner || isAdmin)
            return org.Slug != MagicStrings.PublicOrganizationSlug || access != AccessType.Delete;

        // Regular organization user access
        if (access == AccessType.Delete)
            return false;

        var orgUser = org.Users.FirstOrDefault(u => u.UserId == user.Id);
        return orgUser != null && !await IsUserDeactivated(user);
    }
}