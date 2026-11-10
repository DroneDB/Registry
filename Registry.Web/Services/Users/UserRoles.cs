using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Users;

/// <summary>
/// The roles and the role assignments an administrator manages. The system roles (<see cref="SystemRoles"/>) cannot be
/// deleted; <c>deactivated</c> is added and removed only by <see cref="DisableUser"/> and <see cref="EnableUser"/>
/// (D-043), which work whatever the identity provider.
/// </summary>
public sealed class UserRoles
{
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly UserProvisioning _provisioning;
    private readonly IAuthManager _authManager;

    public UserRoles(ILoginManager loginManager, UserManager<User> userManager, RoleManager<IdentityRole> roleManager,
        UserProvisioning provisioning, IAuthManager authManager)
    {
        _loginManager = loginManager;
        _userManager = userManager;
        _roleManager = roleManager;
        _provisioning = provisioning;
        _authManager = authManager;
    }

    // The member of the create request that names the field (D-047)
    private const string RoleNameField = "roleName";

    private const string LastActiveAdminMessage =
        "Cannot disable the last active administrator: make another user an administrator first";

    /// <summary>Shorthand accessor for the active provider's capability descriptor.</summary>
    private AuthProviderCapabilities Caps => _loginManager.Capabilities;

    public async Task<string[]> GetRoles(CancellationToken ct = default)
    {
        // Identity refuses to create a role without a name
        var roles = _roleManager.Roles.Select(role => role.Name!);

        return await roles.ToArrayAsync(ct);
    }

    public async Task CreateRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new BadRequestException("Role name cannot be empty");

        // The rule of FR-XC-12 (D-007), the one of user names: a role that the delete route cannot name ("a/b") is never made
        if (!NameRules.IsValid(roleName))
            throw new InvalidFieldException(RoleNameField, NameRules.InvalidMessage("Role name"));

        var roleExists = await _roleManager.RoleExistsAsync(roleName);
        if (roleExists)
            throw new BadRequestException($"Role '{roleName}' already exists");

        var result = await _roleManager.CreateAsync(new IdentityRole(roleName));
        if (!result.Succeeded)
            throw new BadRequestException(
                $"Failed to create role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }

    /// <summary>
    /// Deletes a role. <paramref name="roleName"/> is the route value: a name with "/" arrives percent-encoded
    /// ("a%2Fb", the router keeps %2F), so a role that is not found as written is looked up unescaped (FR-ADM-01):
    /// every listed role can be deleted, also a legacy one that the creation rule would refuse today.
    /// </summary>
    public async Task DeleteRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new BadRequestException("Role name cannot be empty");

        var role = await _roleManager.FindByNameAsync(roleName);
        if (role == null && roleName.Contains('%'))
        {
            roleName = Uri.UnescapeDataString(roleName);
            role = string.IsNullOrWhiteSpace(roleName) ? null : await _roleManager.FindByNameAsync(roleName);
        }

        // The server depends on the system roles, in any letter case (Identity finds 'Admin' as 'admin')
        if (SystemRoles.IsSystemRole(roleName))
            throw new BadRequestException($"Cannot delete the system role '{roleName}'");

        if (role == null)
            throw new BadRequestException($"Role '{roleName}' does not exist");

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
            throw new ConflictException(
                $"Failed to delete role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }

    /// <summary>
    /// The roles of a role list that Edit User assigns: without <c>deactivated</c> (D-043: the list neither adds nor
    /// removes it) and without repeats, in any letter case.
    /// </summary>
    private static string[] Assignable(IEnumerable<string>? roles) =>
        [.. (roles ?? []).Where(r => !SystemRoles.IsDeactivated(r)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Refuses the role list of Edit User for <paramref name="userName"/> before anything is changed: a role that does
    /// not exist (Identity would throw when it assigns it), <c>deactivated</c> for a user who is not disabled (only the
    /// disable endpoint adds it), or the user <c>admin</c> without the role <c>admin</c>.
    /// </summary>
    /// <exception cref="BadRequestException">The list is refused.</exception>
    public async Task ValidateUserRoles(string userName, string[]? roles)
    {
        // A disabled user's list may name 'deactivated' (a client that sends the roles back): it changes nothing
        if (await _userManager.FindByNameAsync(userName) is { } user)
            await _provisioning.RefuseDeactivatedAsync(user, roles);

        var wanted = Assignable(roles);

        // Do not allow removing the admin role from the admin user
        if (userName.Equals("admin", StringComparison.OrdinalIgnoreCase) &&
            !wanted.Contains(ApplicationDbContext.AdminRoleName, StringComparer.OrdinalIgnoreCase))
            throw new BadRequestException("Cannot remove admin role from admin user");

        await _provisioning.ValidateRoles(wanted);
    }

    /// <summary>
    /// Gives the user the roles of <paramref name="roles"/>: the roles it lacks are added, the others removed. The
    /// list cannot add or remove the role <c>deactivated</c> (D-043: 400 when it would add it, kept when it is left
    /// out); <c>admin</c> is granted and removed like any other role.
    /// </summary>
    public async Task UpdateUserRoles(string userName, string[]? roles)
    {
        var user = await _userManager.FindByNameAsync(userName);
        if (user == null)
            throw new BadRequestException($"User '{userName}' not found");

        if (Caps.ManagesRolesExternally)
            throw new ForbiddenException(
                "Role assignments are managed by the identity provider (group membership) " +
                "and cannot be modified locally.", noRetry: true);

        // Every name is checked before any change
        await ValidateUserRoles(userName, roles);

        var wanted = Assignable(roles);

        var current = (await _userManager.GetRolesAsync(user)).Where(r => !SystemRoles.IsDeactivated(r)).ToList();
        var removed = current.Where(r => !wanted.Contains(r, StringComparer.OrdinalIgnoreCase)).ToArray();
        var added = wanted.Where(r => !current.Contains(r, StringComparer.OrdinalIgnoreCase)).ToArray();

        if (removed.Length > 0)
        {
            var removeResult = await _userManager.RemoveFromRolesAsync(user, removed);
            if (!removeResult.Succeeded)
                throw new BadRequestException(
                    $"Failed to remove current roles: {string.Join(", ", removeResult.Errors.Select(e => e.Description))}");
        }

        if (added.Length > 0)
        {
            var addResult = await _userManager.AddToRolesAsync(user, added);
            if (!addResult.Succeeded)
                throw new BadRequestException(
                    $"Failed to add new roles: {string.Join(", ", addResult.Errors.Select(e => e.Description))}");
        }
    }

    /// <summary>
    /// Disables a user: the role <c>deactivated</c> blocks sign-in, and the role change drops the cached authorization
    /// of the user, so the sessions it has lose access at once. Disabling a disabled user changes nothing. Also for a
    /// user of an external provider: the deactivation is local.
    /// </summary>
    /// <exception cref="NotFoundException">The user does not exist.</exception>
    /// <exception cref="BadRequestException">The user is the caller, or the last active administrator.</exception>
    public async Task DisableUser(string userName)
    {
        var user = await FindUser(userName);

        if (user.Id == await _authManager.GetCurrentUserId())
            throw new BadRequestException("You cannot disable your own account");

        if (await IsLastActiveAdmin(user))
            throw new BadRequestException(LastActiveAdminMessage);

        if (await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName))
            return;

        // Seeded at start-up; a database that predates the seeding of every provider may lack it
        if (!await _roleManager.RoleExistsAsync(ApplicationDbContext.DeactivatedRoleName))
            CheckResult(await _roleManager.CreateAsync(new IdentityRole(ApplicationDbContext.DeactivatedRoleName)),
                "create the role 'deactivated'");

        var wasAdmin = await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);
        CheckResult(await _userManager.AddToRoleAsync(user, ApplicationDbContext.DeactivatedRoleName),
            $"disable user '{userName}'");

        // Check, then act: two administrators disabling each other at once both pass the check above. Whoever finds
        // no active administrator left after its change takes it back
        if (wasAdmin && !await AnyActiveAdmin())
        {
            CheckResult(await _userManager.RemoveFromRoleAsync(user, ApplicationDbContext.DeactivatedRoleName),
                $"keep user '{userName}' enabled");
            throw new BadRequestException(LastActiveAdminMessage);
        }
    }

    /// <summary>Enables a disabled user (removes the role <c>deactivated</c>); enabling an active user changes nothing.</summary>
    /// <exception cref="NotFoundException">The user does not exist.</exception>
    public async Task EnableUser(string userName)
    {
        var user = await FindUser(userName);

        if (!await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName))
            return;

        CheckResult(await _userManager.RemoveFromRoleAsync(user, ApplicationDbContext.DeactivatedRoleName),
            $"enable user '{userName}'");
    }

    private async Task<User> FindUser(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new BadRequestException("User name cannot be empty");

        return await _userManager.FindByNameAsync(userName)
               ?? throw new NotFoundException($"User '{userName}' does not exist");
    }

    // An active administrator that no other active administrator backs up
    private async Task<bool> IsLastActiveAdmin(User user)
    {
        if (!await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName) ||
            await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName))
            return false;

        return (await ActiveAdminIds()).All(id => id == user.Id);
    }

    private async Task<bool> AnyActiveAdmin() => (await ActiveAdminIds()).Count > 0;

    // The ids of the administrators who are not disabled
    private async Task<List<string>> ActiveAdminIds()
    {
        var disabled = (await _userManager.GetUsersInRoleAsync(ApplicationDbContext.DeactivatedRoleName))
            .Select(u => u.Id).ToHashSet();
        var admins = await _userManager.GetUsersInRoleAsync(ApplicationDbContext.AdminRoleName);
        return [.. admins.Select(a => a.Id).Where(id => !disabled.Contains(id))];
    }

    private static void CheckResult(IdentityResult result, string action)
    {
        if (!result.Succeeded)
            throw new ConflictException(
                $"Cannot {action}: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }
}
