using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>The roles and the role assignments an administrator manages.</summary>
public sealed class UserRoles
{
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public UserRoles(ILoginManager loginManager, UserManager<User> userManager, RoleManager<IdentityRole> roleManager)
    {
        _loginManager = loginManager;
        _userManager = userManager;
        _roleManager = roleManager;
    }

    /// <summary>Shorthand accessor for the active provider's capability descriptor.</summary>
    private AuthProviderCapabilities Caps => _loginManager.Capabilities;

    public async Task<string[]> GetRoles(CancellationToken ct = default)
    {
        var roles = _roleManager.Roles.Select(role => role.Name);

        return await roles.ToArrayAsync(ct);
    }

    public async Task CreateRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new BadRequestException("Role name cannot be empty");

        var roleExists = await _roleManager.RoleExistsAsync(roleName);
        if (roleExists)
            throw new BadRequestException($"Role '{roleName}' already exists");

        var result = await _roleManager.CreateAsync(new IdentityRole(roleName));
        if (!result.Succeeded)
            throw new BadRequestException(
                $"Failed to create role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }

    public async Task DeleteRole(string roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            throw new BadRequestException("Role name cannot be empty");

        // Do not allow deletion of the admin role
        if (roleName.Equals(ApplicationDbContext.AdminRoleName, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("Cannot delete the admin role");

        var role = await _roleManager.FindByNameAsync(roleName);
        if (role == null)
            throw new BadRequestException($"Role '{roleName}' does not exist");

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
            throw new ConflictException(
                $"Failed to delete role: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }

    public async Task UpdateUserRoles(string userName, string[] roles)
    {
        var user = await _userManager.FindByNameAsync(userName);
        if (user == null)
            throw new BadRequestException($"User '{userName}' not found");

        // Do not allow removing the admin role from the admin user
        if (userName.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            if (!roles.Contains(ApplicationDbContext.AdminRoleName, StringComparer.OrdinalIgnoreCase))
                throw new BadRequestException("Cannot remove admin role from admin user");
        }

        if (Caps.ManagesRolesExternally)
            throw new ForbiddenException(
                "Role assignments are managed by the identity provider (group membership) " +
                "and cannot be modified locally.", noRetry: true);

        var currentRoles = await _userManager.GetRolesAsync(user);

        // Remove all current roles
        if (currentRoles.Any())
        {
            var removeResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removeResult.Succeeded)
                throw new BadRequestException(
                    $"Failed to remove current roles: {string.Join(", ", removeResult.Errors.Select(e => e.Description))}");
        }

        // Add new roles
        if (roles?.Length > 0)
        {
            var addResult = await _userManager.AddToRolesAsync(user, roles);
            if (!addResult.Succeeded)
                throw new BadRequestException(
                    $"Failed to add new roles: {string.Join(", ", addResult.Errors.Select(e => e.Description))}");
        }
    }
}
