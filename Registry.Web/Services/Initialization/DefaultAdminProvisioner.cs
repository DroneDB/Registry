using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Registry.Web.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// The default admin of the web host (out of <see cref="DatabaseInitializer"/>): its roles, its user and its
/// organization, brought in line with the configuration at every start. An identity managed by an external provider
/// has no local default admin.
/// </summary>
internal sealed class DefaultAdminProvisioner
{
    private readonly RegistryContext _registry;
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IdentityProviderOptions _identityProvider;
    private readonly ILogger<DefaultAdminProvisioner> _logger;

    public DefaultAdminProvisioner(RegistryContext registry, ILoginManager loginManager,
        UserManager<User> userManager, RoleManager<IdentityRole> roleManager,
        IOptions<IdentityProviderOptions> identityProvider, ILogger<DefaultAdminProvisioner> logger)
    {
        _registry = registry;
        _loginManager = loginManager;
        _userManager = userManager;
        _roleManager = roleManager;
        _identityProvider = identityProvider.Value;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken token)
    {
        // BEHAVIOR CHANGE (deliberate): when an external identity provider is active
        // (LDAP or Remote), skip creating a local default admin. Admin privileges are granted
        // via the provider's group mapping instead. This change also affects Remote mode.
        if (!_loginManager.Capabilities.SupportsLocalUserManagement)
        {
            _logger.LogInformation(
                "Default admin creation skipped: identity is managed by an external provider. " +
                "Admin privileges are assigned via the provider's group/role mapping.");
            return;
        }

        var userManager = _userManager;
        var roleManager = _roleManager;
        var context = _registry;

        var defaultAdmin = _identityProvider.DefaultAdmin!;

        _logger.LogInformation("Setting up default admin user '{Username}'", defaultAdmin.UserName);

        // Ensure admin role exists
        await EnsureRoleExistsAsync(roleManager, ApplicationDbContext.AdminRoleName);
        await EnsureRoleExistsAsync(roleManager, ApplicationDbContext.DeactivatedRoleName);

        // Get or create admin user
        var adminUser = await userManager.Users.FirstOrDefaultAsync(usr => usr.UserName == defaultAdmin.UserName, token);

        if (adminUser == null)
        {
            adminUser = await CreateAdminUserAsync(userManager, defaultAdmin);
        }
        else
        {
            await UpdateAdminUserAsync(userManager, adminUser, defaultAdmin);
        }

        // Ensure admin organization exists
        await EnsureAdminOrganizationAsync(context, adminUser, defaultAdmin, token);

        _logger.LogInformation("Default admin setup completed");
    }

    private async Task EnsureRoleExistsAsync(RoleManager<IdentityRole> roleManager, string roleName)
    {
        var role = await roleManager.FindByNameAsync(roleName);

        if (role != null)
            return;

        _logger.LogInformation("Creating role '{RoleName}'", roleName);

        role = new IdentityRole(roleName);
        var result = await roleManager.CreateAsync(role);

        if (!result.Succeeded)
            throw new InvalidOperationException($"Cannot create role '{roleName}': {result.Errors.ToErrorString()}");
    }

    private async Task<User> CreateAdminUserAsync(UserManager<User> userManager, AdminInfo defaultAdmin)
    {
        _logger.LogInformation("Creating default admin user '{Username}'", defaultAdmin.UserName);

        var adminUser = new User
        {
            Email = defaultAdmin.Email,
            UserName = defaultAdmin.UserName
        };

        var result = await userManager.CreateAsync(adminUser, defaultAdmin.Password);

        if (!result.Succeeded)
            throw new InvalidOperationException($"Cannot create default admin: {result.Errors?.ToErrorString()}");

        result = await userManager.AddToRoleAsync(adminUser, ApplicationDbContext.AdminRoleName);

        if (!result.Succeeded)
            throw new InvalidOperationException($"Cannot add admin to admin role: {result.Errors?.ToErrorString()}");

        return adminUser;
    }

    /// <summary>
    /// Brings an existing default admin in line with the configuration: admin role and email.
    /// The password is seeded only when the admin is created (<see cref="CreateAdminUserAsync"/>):
    /// resetting it here on every start would undo the admin's own password change and rotate the
    /// security stamp, which logs the admin out.
    /// </summary>
    private async Task UpdateAdminUserAsync(UserManager<User> userManager, User adminUser, AdminInfo defaultAdmin)
    {
        _logger.LogDebug("Updating existing admin user '{Username}'", defaultAdmin.UserName);

        // Ensure admin has admin role
        if (!await userManager.IsInRoleAsync(adminUser, ApplicationDbContext.AdminRoleName))
        {
            _logger.LogInformation("Adding admin role to user '{Username}'", defaultAdmin.UserName);
            var result = await userManager.AddToRoleAsync(adminUser, ApplicationDbContext.AdminRoleName);

            if (!result.Succeeded)
                throw new InvalidOperationException($"Cannot add admin to admin role: {result.Errors?.ToErrorString()}");
        }

        // Update email through the identity store (the user belongs to ApplicationDbContext, not
        // RegistryContext). UpdateAsync refreshes NormalizedEmail and keeps the security stamp.
        if (!string.Equals(adminUser.Email, defaultAdmin.Email, StringComparison.Ordinal))
        {
            _logger.LogInformation("Updating email of admin user '{Username}'", defaultAdmin.UserName);
            adminUser.Email = defaultAdmin.Email;

            var updateResult = await userManager.UpdateAsync(adminUser);

            if (!updateResult.Succeeded)
                throw new InvalidOperationException($"Cannot update admin email: {updateResult.Errors?.ToErrorString()}");
        }
    }

    private async Task EnsureAdminOrganizationAsync(RegistryContext context, User adminUser, AdminInfo defaultAdmin, CancellationToken token)
    {
        var adminOrgSlug = defaultAdmin.UserName.ToSlug();
        var org = await context.Organizations.FirstOrDefaultAsync(o => o.Slug == adminOrgSlug, token);

        if (org != null)
        {
            _logger.LogDebug("Admin organization '{OrgSlug}' already exists", adminOrgSlug);
            return;
        }

        _logger.LogInformation("Creating admin organization '{OrgSlug}'", adminOrgSlug);

        org = new Organization
        {
            Slug = adminOrgSlug,
            Name = $"{defaultAdmin.UserName} organization",
            CreationDate = DateTime.UtcNow,
            Description = null,
            IsPublic = false,
            OwnerId = adminUser.Id
        };

        await context.Organizations.AddAsync(org, token);
        await context.SaveChangesAsync(token);
    }
}
