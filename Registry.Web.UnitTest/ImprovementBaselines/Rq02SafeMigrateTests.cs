#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Data;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-02 item 4c (<c>SafeMigrateAsync</c>): a database that has tables but no
/// <c>__EFMigrationsHistory</c> (a deployment that predates migrations) gets the baseline migration
/// stamped and then the remaining migrations applied in the same run. Before the fix the method
/// returned right after stamping, so the schema stayed at the baseline until the next start.
/// <para>
/// Assumption (shared with the production code): a history-less database is exactly at the baseline
/// (first) migration. Covered on SQLite with the real <see cref="RegistryContext"/> migrations; MySQL
/// (non-transactional DDL), for both contexts, in <c>Rq02MySqlLiveTests</c> (Registry.Web.IntegrationTest).
/// </para>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq02SafeMigrateTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq02SafeMigrateTests
{
    private const string SqliteMigrationsAssembly = "Registry.Web.Data.SqliteMigrations";

    private string _root = null!;
    private string _connectionString = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rq02-safe-migrate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _connectionString = $"Data Source={Path.Combine(_root, "registry.db")};Pooling=False";
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private RegistryContext CreateContext() =>
        new(new DbContextOptionsBuilder<RegistryContext>()
            .UseSqlite(_connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly))
            .Options);

    [Test]
    public async Task SafeMigrate_LegacyDatabaseWithoutHistory_IsBroughtToTheLatestMigration()
    {
        string[] allMigrations;

        // A legacy deployment: the baseline schema, no migrations history
        await using (var legacy = CreateContext())
        {
            allMigrations = legacy.Database.GetMigrations().ToArray();
            allMigrations.Length.ShouldBeGreaterThan(1, "the scenario needs migrations after the baseline");

            await legacy.GetService<IMigrator>().MigrateAsync(allMigrations[0]);
            await legacy.Database.ExecuteSqlRawAsync(
                $"DROP TABLE \"{HistoryRepository.DefaultTableName}\"");
        }

        await using (var context = CreateContext())
        {
            await context.Database.SafeMigrateAsync();
        }

        await using (var check = CreateContext())
        {
            (await check.Database.GetPendingMigrationsAsync()).ShouldBeEmpty(
                "every migration after the stamped baseline must be applied in the same run");
            (await check.Database.GetAppliedMigrationsAsync()).ShouldBe(allMigrations);
        }
    }
}
