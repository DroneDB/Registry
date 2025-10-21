using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Handles database initialization including migrations, initial data creation, and default admin setup.
/// </summary>
internal class DatabaseInitializer
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public DatabaseInitializer(IServiceProvider services, ILogger logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InitializeAsync(CancellationToken token)
    {
        _logger.LogInformation("Database initialization starting");

        await InitializeIdentityDatabaseAsync(token);
        await InitializeRegistryDatabaseAsync(token);

        _logger.LogInformation("Database initialization completed");
    }

    private async Task InitializeIdentityDatabaseAsync(CancellationToken token)
    {
        var context = _services.GetRequiredService<ApplicationDbContext>();
        var configuration = _services.GetRequiredService<IConfiguration>();

        var isSqlite = context.Database.IsSqlite();

        if (isSqlite)
        {
            var connectionString = configuration.GetConnectionString(MagicStrings.IdentityConnectionName);
            CommonUtils.EnsureFolderCreated(connectionString);
        }

        if (isSqlite || context.Database.IsMySql())
        {
            _logger.LogInformation("Running identity database migrations");
            await context.Database.SafeMigrateAsync(token);
        }
    }

    private async Task InitializeRegistryDatabaseAsync(CancellationToken token)
    {
        var context = _services.GetRequiredService<RegistryContext>();
        var configuration = _services.GetRequiredService<IConfiguration>();

        var isSqlite = context.Database.IsSqlite();

        if (isSqlite)
        {
            var connectionString = configuration.GetConnectionString(MagicStrings.RegistryConnectionName);
            CommonUtils.EnsureFolderCreated(connectionString);
        }

        if (isSqlite || context.Database.IsMySql())
        {
            _logger.LogInformation("Running registry database migrations");
            await context.Database.SafeMigrateAsync(token);
        }

        await CreateInitialDataAsync(context, token);
        await CreateDefaultAdminAsync(token);
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
            CreationDate = DateTime.Now,
            Description = "Organization",
            IsPublic = true,
            OwnerId = null
        };

        var ds = new Dataset
        {
            Slug = MagicStrings.DefaultDatasetSlug,
            CreationDate = DateTime.Now,
            InternalRef = Guid.NewGuid()
        };

        entity.Datasets = [ds];

        context.Organizations.Add(entity);
        await context.SaveChangesAsync(token);

        _logger.LogInformation("Initial data created successfully");
    }

    private async Task CreateDefaultAdminAsync(CancellationToken token)
    {
        var userManager = _services.GetRequiredService<UserManager<User>>();
        var roleManager = _services.GetRequiredService<RoleManager<IdentityRole>>();
        var appSettings = _services.GetRequiredService<IOptions<AppSettings>>().Value;
        var context = _services.GetRequiredService<RegistryContext>();

        var defaultAdmin = appSettings.DefaultAdmin;

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
            await UpdateAdminUserAsync(userManager, adminUser, defaultAdmin, context, token);
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

    private async Task UpdateAdminUserAsync(UserManager<User> userManager, User adminUser, AdminInfo defaultAdmin, RegistryContext context, CancellationToken token)
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

        // Update password
        var removeResult = await userManager.RemovePasswordAsync(adminUser);

        if (!removeResult.Succeeded)
            throw new InvalidOperationException($"Cannot remove password for admin: {removeResult.Errors?.ToErrorString()}");

        var addResult = await userManager.AddPasswordAsync(adminUser, defaultAdmin.Password);

        if (!addResult.Succeeded)
            throw new InvalidOperationException($"Cannot set password for admin: {addResult.Errors?.ToErrorString()}");

        // Update email
        adminUser.Email = defaultAdmin.Email;
        await context.SaveChangesAsync(token);
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
            CreationDate = DateTime.Now,
            Description = null,
            IsPublic = false,
            OwnerId = adminUser.Id
        };

        await context.Organizations.AddAsync(org, token);
        await context.SaveChangesAsync(token);
    }
}
