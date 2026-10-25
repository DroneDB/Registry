#nullable enable
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
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
/// On SQLite: the migration that adds the unique <c>(OrganizationSlug, Slug)</c> index of <c>Datasets</c> first repairs
/// the datasets that share a slug (the later copies are renamed, nothing is deleted: <see cref="DuplicateDatasetSlugs"/>),
/// so it cannot fail on a database that already holds duplicates, drops the index on the <c>Organizations</c> primary key,
/// and can be undone. The MariaDB counterpart is <c>DatasetSlugMigrationMariaDbTests</c> (Registry.Web.IntegrationTest).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetSlugMigrationTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class DatasetSlugMigrationTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("datasetslugmigration-slugs-").FullName;

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private ServiceProvider Services(string name) =>
        HostDbContexts.Build<RegistryContext>(DbProvider.Sqlite,
            $"Data Source={Path.Combine(_root, name + ".db")};Mode=ReadWriteCreate");

    private static RegistryContext Context(ServiceProvider services) =>
        services.CreateScope().ServiceProvider.GetRequiredService<RegistryContext>();

    [Test]
    public async Task DuplicateSlugs_AreRepaired_ThenTheIndexRefusesNewOnes()
    {
        await using var services = Services("dup");
        using var context = Context(services);
        await DuplicateDatasetSlugs.SeedAsync(context);

        await context.GetService<IMigrator>().MigrateAsync();

        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        await DuplicateDatasetSlugs.AssertRepairedAsync(context, caseInsensitive: false);
        await DuplicateDatasetSlugs.AssertUniqueIndexRefusesADuplicateAsync(context);
        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "Organizations")).ShouldNotContain("IX_Organizations_Slug",
            "the primary key is the index of the slug");
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Test]
    public async Task ADatabaseWithoutDuplicates_MigratesUnchanged()
    {
        await using var services = Services("clean");
        using var context = Context(services);
        await context.GetService<IMigrator>().MigrateAsync(DuplicateDatasetSlugs.PreviousMigration(context));
        await DuplicateDatasetSlugs.Execute(context,
            "INSERT INTO Organizations (Slug, Name, CreationDate, IsPublic) VALUES ('org', 'org', '2024-01-01 00:00:00', 1)");
        foreach (var (id, slug) in new[] { (1, "a"), (2, "b"), (3, "a_1"), (4, "c") })
            await DuplicateDatasetSlugs.Execute(context,
                $"INSERT INTO Datasets (Id, CreationDate, InternalRef, OrganizationSlug, Slug) VALUES ({id}, '2024-01-01 00:00:00', '00000000-0000-0000-0000-{id:D12}', 'org', '{slug}')");

        await context.GetService<IMigrator>().MigrateAsync();

        (await context.Database.SqlQueryRaw<string>("SELECT Slug AS Value FROM Datasets ORDER BY Id").ToListAsync())
            .ShouldBe(["a", "b", "a_1", "c"]);
    }

    [Test]
    public async Task AnEmptyDatabase_MigratesToTheLatest_WithTheIndexes()
    {
        await using var services = Services("empty");
        using var context = Context(services);

        await context.GetService<IMigrator>().MigrateAsync();

        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "Datasets")).ShouldContain("IX_Datasets_OrganizationSlug_Slug");
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Test]
    public async Task Down_RestoresTheIndexes_KeepsTheRepairedSlugs_AndUpRunsAgain()
    {
        await using var services = Services("down");
        using var context = Context(services);
        await DuplicateDatasetSlugs.SeedAsync(context);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();

        await migrator.MigrateAsync(DuplicateDatasetSlugs.PreviousMigration(context));

        var datasets = await DuplicateDatasetSlugs.IndexNamesAsync(context, "Datasets");
        datasets.ShouldNotContain("IX_Datasets_OrganizationSlug_Slug");
        datasets.ShouldContain("IX_Datasets_OrganizationSlug", "the foreign key has its own index again");
        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "Organizations")).ShouldContain("IX_Organizations_Slug");
        // A duplicate can be written again once the index is gone: the next Up repairs it
        await DuplicateDatasetSlugs.Execute(context,
            "INSERT INTO Datasets (Id, CreationDate, InternalRef, OrganizationSlug, Slug) VALUES (200, '2024-03-01 00:00:00', '00000000-0000-0000-0000-000000000200', 'alpha', 'survey')");

        await migrator.MigrateAsync();

        (await context.Database.SqlQueryRaw<string>("SELECT Slug AS Value FROM Datasets WHERE Id = 200").SingleAsync())
            .ShouldBe("survey_200");
        await DuplicateDatasetSlugs.AssertUniqueIndexRefusesADuplicateAsync(context);
    }
}
