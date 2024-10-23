using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Managers;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Handles dataset-specific access control
/// </summary>
public class DatasetAccessControl : AccessControlBase
{
    private readonly IDdbManager _ddbManager;

    public DatasetAccessControl(UserManager<User> usersManager, ILogger logger, IDdbManager ddbManager)
        : base(usersManager, logger)
    {
        _ddbManager = ddbManager;
    }

    public async Task<bool> CanAccessDataset(Dataset dataset, AccessType access, User user)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var org = dataset.Organization;

        // Check dataset visibility
        var ddb = _ddbManager.Get(org.Slug, dataset.InternalRef);
        var meta = ddb.Meta.GetSafe();
        var isPublicOrUnlisted = meta.IsPublicOrUnlisted();

        // Anonymous access check
        if (user == null)
        {
            // Anonymous users can access only public or unlisted datasets with Read access
            if (access != AccessType.Read || !isPublicOrUnlisted) return false;

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
            // Admin can access any dataset and perform any action
            return true;
        }

        // Owner privileges
        if (org.OwnerId == user.Id)
        {
            // Owner can access and modify datasets
            return true;
        }

        // Organization member access
        var orgUser = org.Users.FirstOrDefault(u => u.UserId == user.Id);
        if (orgUser == null)
        {
            // Non-organization members can only read public or unlisted datasets
            return access == AccessType.Read && isPublicOrUnlisted;
        }

        var orgUserDetails = await UsersManager.FindByIdAsync(orgUser.UserId);
        if (orgUserDetails == null)
        {
            Logger.LogInformation("User {UserId} not found for organization {OrganizationSlug}",
                orgUser.UserId, org.Slug);
            return false;
        }

        // Only the owner can delete datasets
        if (access == AccessType.Delete)
            return false;

        return !await IsUserDeactivated(orgUserDetails);
    }
}
