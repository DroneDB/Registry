using System;
using System.Collections.Generic;
using System.Linq;
using Registry.Web.Identity;

namespace Registry.Web.Utilities;

/// <summary>
/// The roles the server itself depends on (FR-ADM-04, D-043): <see cref="ApplicationDbContext.AdminRoleName"/> grants
/// administration and <see cref="ApplicationDbContext.DeactivatedRoleName"/> blocks sign-in. They cannot be deleted.
/// Edit User still grants and removes <c>admin</c>; only the disable and enable endpoints change <c>deactivated</c>.
/// <c>GET /sys/features</c> publishes the names, so clients mark them instead of naming them.
/// </summary>
public static class SystemRoles
{
    /// <summary>The system role names, as stored.</summary>
    public static IReadOnlyList<string> Names { get; } =
        [ApplicationDbContext.AdminRoleName, ApplicationDbContext.DeactivatedRoleName];

    /// <summary>Whether <paramref name="roleName"/> is a system role, in any letter case (Identity's role lookup ignores it).</summary>
    public static bool IsSystemRole(string? roleName) =>
        roleName != null && Names.Contains(roleName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The 400 message of a role list (create user, Edit User, user metadata) that would add <c>deactivated</c>.</summary>
    public const string DeactivatedInRoleListMessage =
        "The role 'deactivated' cannot be given in a role list: disable the user instead " +
        "(POST /users/{userName}/disable)";

    /// <summary>Whether <paramref name="roleName"/> is the role that blocks sign-in, in any letter case.</summary>
    public static bool IsDeactivated(string? roleName) =>
        string.Equals(roleName, ApplicationDbContext.DeactivatedRoleName, StringComparison.OrdinalIgnoreCase);
}
