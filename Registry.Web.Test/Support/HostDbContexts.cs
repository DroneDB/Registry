#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;

namespace Registry.Web.Test.Support;

/// <summary>
/// The two EF contexts registered exactly as the host registers them
/// (<see cref="StartupExtenders.AddDbContextWithProvider{T}"/>: provider, connection string name,
/// <c>Registry.Web.{Data|Identity}.{Sqlite|MySql}Migrations</c> assembly, MySQL version cache).
/// </summary>
public static class HostDbContexts
{
    /// <summary>The host's connection string name and migrations namespace of <typeparamref name="TContext"/>.</summary>
    public static (string ConnectionName, string MigrationsNamespace) Registration<TContext>() where TContext : DbContext =>
        typeof(TContext) == typeof(RegistryContext) ? (MagicStrings.RegistryConnectionName, "Data")
        : typeof(TContext) == typeof(ApplicationDbContext) ? (MagicStrings.IdentityConnectionName, "Identity")
        : throw new ArgumentOutOfRangeException(nameof(TContext), typeof(TContext).Name, "Not a Registry context");

    /// <summary>The migrations assembly the host configures for <typeparamref name="TContext"/> on <paramref name="provider"/>.</summary>
    public static string MigrationsAssembly<TContext>(DbProvider provider) where TContext : DbContext =>
        $"Registry.Web.{Registration<TContext>().MigrationsNamespace}." +
        $"{(provider == DbProvider.Sqlite ? "Sqlite" : "MySql")}Migrations";

    /// <summary>
    /// Registers <typeparamref name="TContext"/> on <paramref name="connectionString"/> as the host does.
    /// <paramref name="pinnedMySqlVersion"/> is <c>AppSettings:MySqlServerVersion</c> (null probes the server).
    /// </summary>
    public static IServiceCollection AddHostDbContext<TContext>(this IServiceCollection services, DbProvider provider,
        string connectionString, string? pinnedMySqlVersion = null) where TContext : DbContext
    {
        var (connectionName, migrationsNamespace) = Registration<TContext>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{connectionName}"] = connectionString,
            [MySqlServerVersionCache.PinnedVersionKey] = pinnedMySqlVersion
        }).Build();

        services.AddDbContextWithProvider<TContext>(configuration, provider, connectionName, migrationsNamespace);
        return services;
    }

    /// <summary>A service provider with only <typeparamref name="TContext"/>, registered as the host does.</summary>
    public static ServiceProvider Build<TContext>(DbProvider provider, string connectionString,
        string? pinnedMySqlVersion = null) where TContext : DbContext =>
        new ServiceCollection().AddHostDbContext<TContext>(provider, connectionString, pinnedMySqlVersion)
            .BuildServiceProvider();
}
