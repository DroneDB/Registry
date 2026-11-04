using System;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Registry.Web.Data;

/// <summary>
/// What every <c>IDesignTimeDbContextFactory</c> of the four migrations projects does (the EF Core tools build the
/// context from the migrations assembly: no startup project, no configuration, no host): the context on the provider of the
/// project (<see cref="DbProviderConfigurator"/>), with the migrations assembly of that project.
/// <para>
/// A design-time run never reads the application's configuration. The connection string is
/// <see cref="ConnectionEnvironmentVariable"/> (no password is kept in the repository; the fallback is a throw-away local
/// database that <c>migrations add</c> never opens) and the MySQL server version is
/// <see cref="MySqlVersionEnvironmentVariable"/>, default <see cref="DefaultMySqlVersion"/>: pinned, so a design-time run
/// never probes a server.
/// </para>
/// </summary>
public static class DesignTimeContexts
{
    /// <summary>The connection string of <c>dotnet ef database update</c> and friends.</summary>
    public const string ConnectionEnvironmentVariable = "REGISTRY_EF_CONNECTION";

    /// <summary>The MySQL/MariaDB server version the model is generated for, e.g. <c>8.0.36-mysql</c>.</summary>
    public const string MySqlVersionEnvironmentVariable = "REGISTRY_EF_MYSQL_VERSION";

    /// <summary>Production runs MariaDB 10.6 (docker/production).</summary>
    public const string DefaultMySqlVersion = "10.6.0-mariadb";

    private const string DefaultSqliteConnection = "Data Source=registry-design-time.db;Mode=ReadWriteCreate";
    private const string DefaultMySqlConnection = "Server=127.0.0.1;Database=RegistryDesignTime;Uid=root;";

    /// <param name="create">The constructor of the context over its options.</param>
    /// <param name="provider">The provider of the migrations project.</param>
    /// <param name="migrationsAssembly">The assembly of the factory: the migrations project itself.</param>
    public static TContext Create<TContext>(Func<DbContextOptions<TContext>, TContext> create, DbProvider provider,
        Assembly migrationsAssembly) where TContext : DbContext
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connection))
            connection = provider == DbProvider.Sqlite ? DefaultSqliteConnection : DefaultMySqlConnection;

        var version = Environment.GetEnvironmentVariable(MySqlVersionEnvironmentVariable);
        var serverVersion = ServerVersion.Parse(string.IsNullOrWhiteSpace(version) ? DefaultMySqlVersion : version.Trim());

        var builder = new DbContextOptionsBuilder<TContext>();
        DbProviderConfigurator.Configure(builder, provider, connection, migrationsAssembly.GetName().Name!, _ => serverVersion);
        return create(builder.Options);
    }
}
