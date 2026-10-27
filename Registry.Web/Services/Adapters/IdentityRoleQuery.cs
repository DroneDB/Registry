#nullable enable
using System.Linq;
using Registry.Web.Identity;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Role membership read straight from the identity database, for the hosts that have no <c>UserManager</c> (the
/// processing nodes): the one join of the users to the roles, by the normalized role name as Identity stores it.
/// </summary>
internal static class IdentityRoleQuery
{
    /// <summary>The ids of the users in the role <paramref name="roleName"/>, as a query to compose into another one.</summary>
    public static IQueryable<string> UserIdsIn(ApplicationDbContext identity, string roleName)
    {
        var normalized = roleName.ToUpperInvariant();

        return from userRole in identity.UserRoles
               join role in identity.Roles on userRole.RoleId equals role.Id
               where role.NormalizedName == normalized
               select userRole.UserId;
    }
}
