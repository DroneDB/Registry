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
/// The migrations of the identity and registry databases (RQ-12, out of <see cref="DatabaseInitializer"/>): SQLite
/// and MySQL are migrated, any other provider is left alone.
/// </summary>
internal sealed class DatabaseMigrator
{
    private readonly ApplicationDbContext _identity;
    private readonly RegistryContext _registry;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(ApplicationDbContext identity, RegistryContext registry, IConfiguration configuration,
        ILogger<DatabaseMigrator> logger)
    {
        _identity = identity;
        _registry = registry;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Migrates the identity database, then the registry database.</summary>
    public async Task MigrateAsync(CancellationToken token)
    {
        await MigrateAsync(_identity, MagicStrings.IdentityConnectionName, "identity", token);
        await MigrateAsync(_registry, MagicStrings.RegistryConnectionName, "registry", token);
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
}
