using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Auth;

public abstract class AccessControlBase
{
    protected readonly IUserAuthSnapshotProvider Snapshots;
    protected readonly RegistryContext _context;
    protected readonly ILogger Logger;

    protected AccessControlBase(IUserAuthSnapshotProvider snapshots, RegistryContext context, ILogger logger)
    {
        Snapshots = snapshots;
        _context = context;
        Logger = logger;
    }

    /// <summary>
    /// Checks if a user is deactivated
    /// </summary>
    public async Task<bool> IsUserDeactivated(string userId)
    {
        if (userId == null) return false;
        return (await Snapshots.GetAsync(userId)).IsDeactivated;
    }

    /// <summary>
    /// Checks if a user has admin privileges
    /// </summary>
    public async Task<bool> IsUserAdmin(string userId)
    {
        if (userId == null) return false;
        return (await Snapshots.GetAsync(userId)).IsAdmin;
    }

    /// <summary>
    /// Checks that a resource owner exists and is not deactivated
    /// </summary>
    protected async Task<bool> IsOwnerActive(string ownerId)
    {
        return ownerId != null && (await Snapshots.GetAsync(ownerId)).IsActive;
    }
}
