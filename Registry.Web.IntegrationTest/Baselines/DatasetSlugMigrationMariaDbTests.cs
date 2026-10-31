using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// On the MariaDB production runs (<see cref="MariaDbServer"/>, <c>mariadb:10.6</c>, databases created with
/// <c>latin1_general_ci</c>): the same scenario as <c>DatasetSlugMigrationTests</c> (Registry.Web.UnitTest) on SQLite. Here
/// the collation is case-insensitive, so <c>Plot</c> and <c>plot</c> are one slug and the later copy is repaired, and the
/// foreign key of <c>Datasets.OrganizationSlug</c> must keep an index while the old one is replaced by the new one.
/// Set <c>REGISTRY_TEST_MYSQL_CONNECTION</c> to a root connection string to run it against another server (MySQL 8).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~DatasetSlugMigrationMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class DatasetSlugMigrationMariaDbTests
{
    private MariaDbServer _server = null!;

    [SetUp]
    public async Task SetUp() => _server = await MariaDbServer.GetAsync();

    private static RegistryContext Context(ServiceProvider services) =>
        services.CreateScope().ServiceProvider.GetRequiredService<RegistryContext>();

    [Test]
    public async Task DuplicateSlugs_AreRepaired_ThenTheIndexRefusesNewOnes()
    {
        await using var database = await _server.CreateDatabaseAsync("datasetslugmigrationmariadb_dup");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);
        await DuplicateDatasetSlugs.SeedAsync(context);

        await context.GetService<IMigrator>().MigrateAsync();

        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        await DuplicateDatasetSlugs.AssertRepairedAsync(context, caseInsensitive: true);
        await DuplicateDatasetSlugs.AssertUniqueIndexRefusesADuplicateAsync(context);
        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "Organizations")).ShouldNotContain("IX_Organizations_Slug");
        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "Datasets")).ShouldNotContain("IX_Datasets_OrganizationSlug");
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Test]
    public async Task AnEmptyDatabase_MigratesToTheLatest()
    {
        await using var database = await _server.CreateDatabaseAsync("datasetslugmigrationmariadb_empty");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);

        await context.GetService<IMigrator>().MigrateAsync();

        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "Datasets")).ShouldContain("IX_Datasets_OrganizationSlug_Slug");
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Test]
    public async Task Down_RestoresTheIndexes_KeepsTheRepairedSlugs_AndUpRunsAgain()
    {
        await using var database = await _server.CreateDatabaseAsync("datasetslugmigrationmariadb_down");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);
        await DuplicateDatasetSlugs.SeedAsync(context);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();

        await migrator.MigrateAsync(DuplicateDatasetSlugs.PreviousMigration(context));

        var datasets = await DuplicateDatasetSlugs.IndexNamesAsync(context, "Datasets");
        datasets.ShouldNotContain("IX_Datasets_OrganizationSlug_Slug");
        datasets.ShouldContain("IX_Datasets_OrganizationSlug", "the foreign key has its own index again");
        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "Organizations")).ShouldContain("IX_Organizations_Slug");
        await DuplicateDatasetSlugs.Execute(context,
            "INSERT INTO Datasets (Id, CreationDate, InternalRef, OrganizationSlug, Slug) VALUES (200, '2024-03-01 00:00:00', '00000000-0000-0000-0000-000000000200', 'alpha', 'survey')");

        await migrator.MigrateAsync();

        (await context.Database.SqlQueryRaw<string>("SELECT Slug AS Value FROM Datasets WHERE Id = 200").SingleAsync())
            .ShouldBe("survey_200");
        await DuplicateDatasetSlugs.AssertUniqueIndexRefusesADuplicateAsync(context);
    }

    [Test]
    public async Task ADatabaseWithManyDuplicates_IsRepairedInOneStatement()
    {
        await using var database = await _server.CreateDatabaseAsync("datasetslugmigrationmariadb_many");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);
        await context.GetService<IMigrator>().MigrateAsync(DuplicateDatasetSlugs.PreviousMigration(context));
        await DuplicateDatasetSlugs.Execute(context,
            "INSERT INTO Organizations (Slug, Name, CreationDate, IsPublic) VALUES ('big', 'big', '2024-01-01 00:00:00', 1)");
        // 1000 datasets, 20 slugs: 980 of them are later copies
        await DuplicateDatasetSlugs.Execute(context,
            @"INSERT INTO Datasets (Id, CreationDate, InternalRef, OrganizationSlug, Slug)
              WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 1000)
              SELECT i, '2024-01-01 00:00:00', UUID(), 'big', CONCAT('slug', i % 20) FROM n");

        await context.GetService<IMigrator>().MigrateAsync();

        (await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Datasets").SingleAsync()).ShouldBe(1000);
        (await context.Database.SqlQueryRaw<int>("SELECT COUNT(DISTINCT Slug) AS Value FROM Datasets").SingleAsync())
            .ShouldBe(1000);
        (await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Datasets WHERE Slug LIKE 'slug%' AND Slug NOT LIKE '%\\_%'").SingleAsync())
            .ShouldBe(20, "the first dataset of each slug keeps it");
    }
}
