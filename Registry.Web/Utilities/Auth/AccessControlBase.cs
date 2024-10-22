using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;

namespace Registry.Web.Utilities.Auth;

public abstract class AccessControlBase
{
    protected readonly UserManager<User> UsersManager;
    protected readonly ILogger Logger;

    protected AccessControlBase(UserManager<User> usersManager, ILogger logger)
    {
        UsersManager = usersManager;
        Logger = logger;
    }

    /// <summary>
    /// Checks if a user is deactivated
    /// </summary>
    protected async Task<bool> IsUserDeactivated(User user)
    {
        if (user == null) return false;
        return await UsersManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName);
    }

    /// <summary>
    /// Checks if a user has admin privileges
    /// </summary>
    protected async Task<bool> IsUserAdmin(User user)
    {
        if (user == null) return false;
        return await UsersManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);
    }
}