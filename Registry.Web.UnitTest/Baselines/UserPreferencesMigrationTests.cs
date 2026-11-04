using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Common;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The SQLite <c>AddUserPreferences</c> migration, run for real: a user row that exists before the column does reads
/// <c>Preferences</c> as an empty dictionary (<c>{}</c>, the column's default) with its <c>Metadata</c> intact, the
/// column is NOT NULL, and the migration reverts (the user stays). The MySQL migration is covered on MariaDB
/// (<c>UserPreferencesMigrationMariaDbTests</c>, Registry.Web.IntegrationTest).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserPreferencesMigrationTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class UserPreferencesMigrationTests
{
    private const string BeforeMigration = "20261021015351_RepairUserMetadata";
    private const string AddUserPreferences = "20261026095455_AddUserPreferences";
    private const string StoredMetadata = """{"maxStorage":5,"roles":["editor"]}""";

    private string _root = null!;
    private ServiceProvider _services = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "userpreferencesmigration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "identity.db"), Pooling = false
        }.ToString();
        _services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Sqlite, connectionString);
    }

    [TearDown]
    public void TearDown()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task AUserThatExistedBeforeTheMigration_ReadsEmptyPreferences_AndKeepsItsMetadata()
    {
        using (var scope = _services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.GetService<IMigrator>().MigrateAsync(BeforeMigration);
            (await Columns(context)).ShouldNotContain("Preferences");
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO AspNetUsers (Id, UserName, Metadata, EmailConfirmed, PhoneNumberConfirmed, " +
                "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('old-user', 'old', {0}, 0, 0, 0, 0, 0)",
                StoredMetadata);
        }

        // As DatabaseInitializer does it
        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.SafeMigrateAsync();

        using var check = _services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await after.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        (await after.Database.GetAppliedMigrationsAsync()).ShouldContain(AddUserPreferences);

        (await after.Database.SqlQuery<string>($"SELECT Preferences AS Value FROM AspNetUsers WHERE Id = 'old-user'")
            .SingleAsync()).ShouldBe("{}");
        var user = await after.Users.AsNoTracking().SingleAsync(u => u.Id == "old-user");
        user.Preferences.ShouldNotBeNull();
        user.Preferences.ShouldBeEmpty();
        user.Metadata.ShouldContainKey("maxStorage");
        user.Metadata["maxStorage"].ShouldBe(5L);
        user.Metadata.ShouldContainKey("roles");
        (await after.Database.SqlQuery<string>($"SELECT Metadata AS Value FROM AspNetUsers WHERE Id = 'old-user'")
            .SingleAsync()).ShouldBe(StoredMetadata, "the metadata text is untouched");
    }

    [Test]
    public async Task TheNewColumn_IsNotNull_WithAnEmptyObjectDefault_SoARowInsertedWithoutItIsEmpty()
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();

        var column = await context.Database.SqlQuery<ColumnInfo>($"""
            SELECT "notnull" AS IsRequired, dflt_value AS DefaultValue FROM pragma_table_info('AspNetUsers') WHERE name = 'Preferences'
            """).SingleAsync();
        column.IsRequired.ShouldBe(1L);
        column.DefaultValue.ShouldBe("'{}'");

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUsers (Id, UserName, Metadata, EmailConfirmed, PhoneNumberConfirmed, " +
            "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('raw-user', 'raw', '{{}}', 0, 0, 0, 0, 0)");
        (await context.Users.AsNoTracking().SingleAsync(u => u.Id == "raw-user")).Preferences.ShouldBeEmpty();
    }

    [Test]
    public async Task TheMigration_Reverts_AndTheUserStays_ThenAppliesAgain()
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUsers (Id, UserName, Metadata, Preferences, EmailConfirmed, PhoneNumberConfirmed, " +
            "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('kept-user', 'kept', {0}, '{{\"map\":{{\"zoom\":3}}}}', 0, 0, 0, 0, 0)",
            StoredMetadata);

        await context.GetService<IMigrator>().MigrateAsync(BeforeMigration);

        (await Columns(context)).ShouldNotContain("Preferences");
        (await context.Database.SqlQuery<string>($"SELECT Metadata AS Value FROM AspNetUsers WHERE Id = 'kept-user'")
            .SingleAsync()).ShouldBe(StoredMetadata);

        await context.Database.MigrateAsync();
        (await context.Database.SqlQuery<string>($"SELECT Preferences AS Value FROM AspNetUsers WHERE Id = 'kept-user'")
            .SingleAsync()).ShouldBe("{}", "the values of a reverted column are gone; the default fills the new one");
    }

    private sealed record ColumnInfo(long IsRequired, string? DefaultValue);

    private static async Task<string[]> Columns(ApplicationDbContext context) =>
        (await context.Database.SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info('AspNetUsers')").ToListAsync())
        .ToArray();
}
