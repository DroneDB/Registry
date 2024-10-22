using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Web.Identity.Models;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Handles resource-specific access control logic
/// </summary>
public class ResourceAccessControl : AccessControlBase
{
    public ResourceAccessControl(UserManager<User> usersManager, ILogger logger)
        : base(usersManager, logger) { }

    /// <summary>
    /// Validates if an owner exists and is not deactivated
    /// </summary>
    public async Task<bool> ValidateOwner(string ownerId, string resourceIdentifier)
    {
        if (ownerId == null) return true;

        var owner = await UsersManager.FindByIdAsync(ownerId);
        if (owner == null)
        {
            Logger.LogInformation("Owner {UserId} not found for resource {ResourceId}",
                ownerId, resourceIdentifier);
            return true;
        }

        return !await IsUserDeactivated(owner);
    }

    /// <summary>
    /// Checks if a user has elevated privileges (admin or owner)
    /// </summary>
    public async Task<bool> HasElevatedPrivileges(User user, string ownerId)
    {
        if (user == null) return false;
        return ownerId == user.Id || await IsUserAdmin(user);
    }
}