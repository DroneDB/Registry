using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Hosting;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Initialization;

/// <summary>
/// Startup task of the web host (<see cref="StartupTaskOrder.Database"/>): migrations of the identity and
/// registry databases, the initial data and the default admin. The processing nodes never migrate.
/// </summary>
internal sealed class DatabaseInitializer : IStartupTask
{
    private readonly DatabaseMigrator _migrator;
    private readonly RegistryContext _registry;
    private readonly DefaultAdminProvisioner _admin;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(DatabaseMigrator migrator, RegistryContext registry, DefaultAdminProvisioner admin,
        ILogger<DatabaseInitializer> logger)
    {
        _migrator = migrator;
        _registry = registry;
        _admin = admin;
        _logger = logger;
    }

    public int Order => StartupTaskOrder.Database;

    public async Task ExecuteAsync(CancellationToken token)
    {
        _logger.LogInformation("Database initialization starting");

        await _migrator.MigrateAsync(token);

        await CreateInitialDataAsync(_registry, token);
        await _admin.EnsureAsync(token);

        _logger.LogInformation("Database initialization completed");
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
}
