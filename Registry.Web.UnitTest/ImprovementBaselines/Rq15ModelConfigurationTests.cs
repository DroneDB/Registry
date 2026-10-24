#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-15: the model is configured by one <see cref="IEntityTypeConfiguration{TEntity}"/> per entity (a new entity is a new
/// class, never an edit of the context), and the EF tools build each context from the migrations project that carries
/// its <see cref="IDesignTimeDbContextFactory{TContext}"/>, with the provider and migrations assembly of that project.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq15ModelConfigurationTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq15ModelConfigurationTests
{
    private static IEnumerable<Type> ConfigurationTypes(Type context) => context.Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>)));

    private static IEnumerable<Type> ConfiguredEntities(Type context) => ConfigurationTypes(context)
        .SelectMany(t => t.GetInterfaces())
        .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
        .Select(i => i.GetGenericArguments()[0]);

    [Test]
    public void EveryEntityOfTheRegistryModel_HasOneConfigurationClass()
    {
        using var context = TestDb.CreateContext<RegistryContext>(_ => { });

        var entities = context.Model.GetEntityTypes().Select(e => e.ClrType).ToList();
        var configured = ConfiguredEntities(typeof(RegistryContext)).ToList();

        configured.ShouldBe(entities, ignoreOrder: true,
            "each entity of the model is configured by exactly one IEntityTypeConfiguration class");
    }

    [Test]
    public void TheUserOfTheIdentityModel_HasItsConfigurationClass()
    {
        // The Identity entities (roles, claims, logins, tokens) are the framework's; the application's own entity is User
        ConfiguredEntities(typeof(ApplicationDbContext)).ShouldBe([typeof(User)]);
    }

    private static IEnumerable<TestCaseData> Factories()
    {
        yield return new TestCaseData(typeof(Registry.Web.Data.SqliteMigrations.RegistryContextFactory), DbProvider.Sqlite)
            .SetName("Registry, Sqlite");
        yield return new TestCaseData(typeof(Registry.Web.Data.MySqlMigrations.RegistryContextFactory), DbProvider.Mysql)
            .SetName("Registry, MySql");
        yield return new TestCaseData(typeof(Registry.Web.Identity.SqliteMigrations.ApplicationDbContextFactory), DbProvider.Sqlite)
            .SetName("Identity, Sqlite");
        yield return new TestCaseData(typeof(Registry.Web.Identity.MySqlMigrations.ApplicationDbContextFactory), DbProvider.Mysql)
            .SetName("Identity, MySql");
    }

    [TestCaseSource(nameof(Factories))]
    public void TheDesignTimeFactory_BuildsItsContext_OnItsProvider_WithItsMigrationsAssembly(Type factoryType,
        DbProvider provider)
    {
        var contextType = factoryType.GetInterfaces().Single(i => i.GetGenericTypeDefinition() == typeof(IDesignTimeDbContextFactory<>))
            .GetGenericArguments()[0];

        var instance = Activator.CreateInstance(factoryType)!;
        var create = factoryType.GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!;
        using var context = (DbContext)create.Invoke(instance, [Array.Empty<string>()])!;

        context.GetType().ShouldBe(contextType);
        var database = contextType == typeof(RegistryContext) ? "Data" : "Identity";
        var migrations = context.GetService<IMigrationsAssembly>();
        migrations.Assembly.GetName().Name.ShouldBe(DbProviderConfigurator.MigrationsAssemblyName(database, provider));
        migrations.Assembly.ShouldBe(factoryType.Assembly, "the factory is in the migrations project it builds for");
        migrations.ModelSnapshot.ShouldNotBeNull();
        (provider == DbProvider.Sqlite ? context.Database.IsSqlite() : context.Database.IsMySql()).ShouldBeTrue();
        context.Database.HasPendingModelChanges().ShouldBeFalse(
            "the contexts of the tools are the contexts the host builds: same model, same snapshot");
    }

    [Test]
    public void TheDesignTimeConnection_IsTheEnvironmentVariable_ElseAThrowAwayLocalDatabase()
    {
        var factory = new Registry.Web.Data.SqliteMigrations.RegistryContextFactory();
        var saved = Environment.GetEnvironmentVariable(DesignTimeContexts.ConnectionEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesignTimeContexts.ConnectionEnvironmentVariable, null);
            using (var context = factory.CreateDbContext([]))
                context.Database.GetConnectionString()!.ShouldContain("registry-design-time.db");

            Environment.SetEnvironmentVariable(DesignTimeContexts.ConnectionEnvironmentVariable,
                "Data Source=from-the-environment.db");
            using (var context = factory.CreateDbContext([]))
                context.Database.GetConnectionString().ShouldBe("Data Source=from-the-environment.db");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesignTimeContexts.ConnectionEnvironmentVariable, saved);
        }
    }

    [Test]
    public void TheMySqlServerVersion_OfADesignTimeRun_IsPinned_NeverProbed()
    {
        var factory = new Registry.Web.Data.MySqlMigrations.RegistryContextFactory();
        var saved = Environment.GetEnvironmentVariable(DesignTimeContexts.MySqlVersionEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesignTimeContexts.MySqlVersionEnvironmentVariable, "8.0.36-mysql");
            using var context = factory.CreateDbContext([]);

            // Nothing is listening on the fallback connection: building the context and its model must not connect
            context.Model.GetEntityTypes().ShouldNotBeEmpty();
            context.GetService<IMigrationsAssembly>().Migrations.ShouldNotBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesignTimeContexts.MySqlVersionEnvironmentVariable, saved);
        }
    }
}
