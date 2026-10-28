using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Handles resource-specific access control logic
/// </summary>
public class ResourceAccessControl : AccessControlBase
{
    public ResourceAccessControl(IUserAuthSnapshotProvider snapshots, RegistryContext context, ILogger logger)
        : base(snapshots, context, logger) { }

    /// <summary>
    /// Checks if a user has elevated privileges (admin or owner)
    /// </summary>
    public async Task<bool> HasElevatedPrivileges(string? userId, string? ownerId)
    {
        if (userId == null) return false;
        return ownerId == userId || await IsUserAdmin(userId);
    }
}
