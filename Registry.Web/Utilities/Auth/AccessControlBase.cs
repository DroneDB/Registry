using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;

namespace Registry.Web.Utilities.Auth;

public abstract class AccessControlBase
{
    protected readonly UserManager<User> UsersManager;
    protected readonly RegistryContext _context;
    protected readonly ILogger Logger;

    protected AccessControlBase(UserManager<User> usersManager, RegistryContext context, ILogger logger)
    {
        UsersManager = usersManager;
        _context = context;
        Logger = logger;
    }

    /// <summary>
    /// Checks if a user is deactivated
    /// </summary>
    public async Task<bool> IsUserDeactivated(User user)
    {
        if (user == null) return false;
        return await UsersManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName);
    }

    /// <summary>
    /// Checks if a user has admin privileges
    /// </summary>
    public async Task<bool> IsUserAdmin(User user)
    {
        if (user == null) return false;
        return await UsersManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);
    }
}