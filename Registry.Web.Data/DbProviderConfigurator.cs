using System;
using Microsoft.EntityFrameworkCore;

namespace Registry.Web.Data;

/// <summary>
/// The one place that turns a <see cref="DbProvider"/>, a connection string and a migrations assembly into the
/// provider configuration of a DbContext: the host (<c>AddDbContextWithProvider</c>) and the design-time factories of the
/// four migrations projects (<see cref="DesignTimeContexts"/>) call it, so <c>dotnet ef</c> and the running host
/// configure SQLite and MySQL/MariaDB the same way.
/// </summary>
public static class DbProviderConfigurator
{
    /// <summary>
    /// The name of the migrations assembly of one database on one provider: <c>Registry.Web.{Data|Identity}.{Sqlite|MySql}Migrations</c>.
    /// </summary>
    /// <param name="database">The database: <c>Data</c> (registry) or <c>Identity</c>.</param>
    public static string MigrationsAssemblyName(string database, DbProvider provider) =>
        $"Registry.Web.{database}.{ProviderName(provider)}Migrations";

    /// <summary>
    /// Configures <paramref name="options"/> for <paramref name="provider"/>. MySQL and MariaDB retry on transient failure
    /// and need the server version: <paramref name="mySqlVersion"/> is asked for it (with the connection string) only
    /// when the provider is MySQL, so a host resolves the version lazily and a design-time run pins it.
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, DbProvider provider,
        string connectionString, string migrationsAssembly, Func<string, ServerVersion> mySqlVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mySqlVersion);

        return provider switch
        {
            DbProvider.Sqlite => options.UseSqlite(connectionString, x => x.MigrationsAssembly(migrationsAssembly)),
            DbProvider.Mysql => options.UseMySql(connectionString, mySqlVersion(connectionString), x =>
            {
                x.EnableRetryOnFailure();
                x.MigrationsAssembly(migrationsAssembly);
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), $"Unrecognised provider: '{provider}'")
        };
    }

    private static string ProviderName(DbProvider provider) => provider switch
    {
        DbProvider.Sqlite => "Sqlite",
        DbProvider.Mysql => "MySql",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), $"Unrecognised provider: '{provider}'")
    };
}
