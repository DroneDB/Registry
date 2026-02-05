using Registry.Common;
using Registry.Web.Services.Managers;

namespace Registry.Web.Models;

/// <summary>
/// Extension methods for OrganizationPermissions
/// </summary>
public static class OrganizationPermissionsExtensions
{
    /// <summary>
    /// Checks if the permission level allows the specified access type
    /// </summary>
    public static bool HasAccess(this OrganizationPermissions permission, AccessType accessType)
    {
        return accessType switch
        {
            AccessType.Read => permission >= OrganizationPermissions.ReadOnly,
            AccessType.Write => permission >= OrganizationPermissions.ReadWrite,
            AccessType.Delete => permission >= OrganizationPermissions.ReadWriteDelete,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the permission level allows member management
    /// </summary>
    public static bool CanManageMembers(this OrganizationPermissions permission)
    {
        return permission >= OrganizationPermissions.Admin;
    }

    /// <summary>
    /// Gets the display name for the permission level
    /// </summary>
    public static string GetDisplayName(this OrganizationPermissions permission)
    {
        return permission switch
        {
            OrganizationPermissions.ReadOnly => "Read Only",
            OrganizationPermissions.ReadWrite => "Read/Write",
            OrganizationPermissions.ReadWriteDelete => "Read/Write/Delete",
            OrganizationPermissions.Admin => "Admin",
            _ => "Unknown"
        };
    }
}
