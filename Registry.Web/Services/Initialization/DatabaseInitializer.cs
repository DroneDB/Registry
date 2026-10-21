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
/// Startup task of the web host (<see cref="StartupTaskOrder.Database"/>): migrations of the identity and
/// registry databases, the initial data and the default admin. The processing nodes never migrate.
/// </summary>
internal sealed class DatabaseInitializer : IStartupTask
{
    private readonly ApplicationDbContext _identity;
    private readonly RegistryContext _registry;
    private readonly IConfiguration _configuration;
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IdentityProviderOptions _identityProvider;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(ApplicationDbContext identity, RegistryContext registry, IConfiguration configuration,
        ILoginManager loginManager, UserManager<User> userManager, RoleManager<IdentityRole> roleManager,
        IOptions<IdentityProviderOptions> identityProvider, ILogger<DatabaseInitializer> logger)
    {
        _identity = identity;
        _registry = registry;
        _configuration = configuration;
        _loginManager = loginManager;
        _userManager = userManager;
        _roleManager = roleManager;
        _identityProvider = identityProvider.Value;
        _logger = logger;
    }

    public int Order => StartupTaskOrder.Database;

    public async Task ExecuteAsync(CancellationToken token)
    {
        _logger.LogInformation("Database initialization starting");

        await MigrateAsync(_identity, MagicStrings.IdentityConnectionName, "identity", token);
        await MigrateAsync(_registry, MagicStrings.RegistryConnectionName, "registry", token);

        await CreateInitialDataAsync(_registry, token);
        await CreateDefaultAdminAsync(token);

        _logger.LogInformation("Database initialization completed");
    }

    private async Task MigrateAsync(DbContext context, string connectionName, string name, CancellationToken token)
    {
        var isSqlite = context.Database.IsSqlite();

        if (isSqlite)
            CommonUtils.EnsureFolderCreated(_configuration.GetConnectionString(connectionName));

        if (isSqlite || context.Database.IsMySql())
        {
            _logger.LogInformation("Running {Database} database migrations", name);
            await context.Database.SafeMigrateAsync(token);
        }
    }

    private async Task CreateInitialDataAsync(RegistryContext context, CancellationToken token)
    {
        // If organizations exist, initial data is already present
        if (await context.Organizations.AnyAsync(token))
        {
            _logger.LogDebug("Initial data already exists, skipping creation");
            return;
        }

        _logger.LogInformation("Creating initial data (public organization)");

        var entity = new Organization
        {
            Slug = MagicStrings.PublicOrganizationSlug,
            Name = MagicStrings.PublicOrganizationSlug.ToPascalCase(false, CultureInfo.InvariantCulture),
            CreationDate = DateTime.UtcNow,
            Description = "Organization",
            IsPublic = true,
            OwnerId = null
        };

        var ds = new Dataset
        {
            Slug = MagicStrings.DefaultDatasetSlug,
            CreationDate = DateTime.UtcNow,
            InternalRef = Guid.NewGuid()
        };

        entity.Datasets = [ds];

        context.Organizations.Add(entity);
        await context.SaveChangesAsync(token);

        _logger.LogInformation("Initial data created successfully");
    }

    private async Task CreateDefaultAdminAsync(CancellationToken token)
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
        await EnsureRoleExistsAsync(roleManager, ApplicationDbContext.AdminRoleName, token);
        await EnsureRoleExistsAsync(roleManager, ApplicationDbContext.DeactivatedRoleName, token);

        // Get or create admin user
        var adminUser = await userManager.Users.FirstOrDefaultAsync(usr => usr.UserName == defaultAdmin.UserName, token);

        if (adminUser == null)
        {
            adminUser = await CreateAdminUserAsync(userManager, defaultAdmin, token);
        }
        else
        {
            await UpdateAdminUserAsync(userManager, adminUser, defaultAdmin);
        }

        // Ensure admin organization exists
        await EnsureAdminOrganizationAsync(context, adminUser, defaultAdmin, token);

        _logger.LogInformation("Default admin setup completed");
    }

    private async Task EnsureRoleExistsAsync(RoleManager<IdentityRole> roleManager, string roleName, CancellationToken token)
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

    private async Task<User> CreateAdminUserAsync(UserManager<User> userManager, AdminInfo defaultAdmin, CancellationToken token)
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
