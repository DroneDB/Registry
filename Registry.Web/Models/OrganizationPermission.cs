using Registry.Common;
using Registry.Web.Services.Managers;

namespace Registry.Web.Models;

/// <summary>
/// Extension methods for OrganizationPermission
/// </summary>
public static class OrganizationPermissionExtensions
{
    /// <summary>
    /// Checks if the permission level allows the specified access type
    /// </summary>
    public static bool HasAccess(this OrganizationPermission permission, AccessType accessType)
    {
        return accessType switch
        {
            AccessType.Read => permission >= OrganizationPermission.ReadOnly,
            AccessType.Write => permission >= OrganizationPermission.ReadWrite,
            AccessType.Delete => permission >= OrganizationPermission.ReadWriteDelete,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the permission level allows member management
    /// </summary>
    public static bool CanManageMembers(this OrganizationPermission permission)
    {
        return permission >= OrganizationPermission.Admin;
    }

    /// <summary>
    /// Gets the display name for the permission level
    /// </summary>
    public static string GetDisplayName(this OrganizationPermission permission)
    {
        return permission switch
        {
            OrganizationPermission.ReadOnly => "Read Only",
            OrganizationPermission.ReadWrite => "Read/Write",
            OrganizationPermission.ReadWriteDelete => "Read/Write/Delete",
            OrganizationPermission.Admin => "Admin",
            _ => "Unknown"
        };
    }
}
