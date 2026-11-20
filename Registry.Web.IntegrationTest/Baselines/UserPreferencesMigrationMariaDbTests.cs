using System.Threading.Tasks;
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
/// The MySQL <c>AddUserPreferences</c> migration on the production MariaDB image (<see cref="MariaDbServer"/>): MySQL
/// refuses a literal default on <c>longtext</c>, so the migration adds the column nullable, fills <c>{}</c> and makes it
/// NOT NULL. A user that exists before it reads <c>Preferences</c> as an empty dictionary with its <c>Metadata</c>
/// intact, and the migration reverts. The SQLite migration runs in <c>UserPreferencesMigrationTests</c>
/// (Registry.Web.UnitTest).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~UserPreferencesMigrationMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class UserPreferencesMigrationMariaDbTests
{
    private const string BeforeMigration = "20261021012759_UserMetadataRequired";
    private const string StoredMetadata = """{"maxStorage":5,"roles":["editor"]}""";

    [Test]
    public async Task UsersThatExistedBeforeTheMigration_ReadEmptyPreferences_AndKeepTheirMetadata()
    {
        var server = await MariaDbServer.GetAsync();
        await using var database = await server.CreateDatabaseAsync("userpreferences_identity");
        using var services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Mysql, database.ConnectionString);

        using (var scope = services.CreateScope())
        {
            var legacy = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await legacy.GetService<IMigrator>().MigrateAsync(BeforeMigration);
            await legacy.Database.ExecuteSqlRawAsync(
                "INSERT INTO AspNetUsers (Id, UserName, Metadata, EmailConfirmed, PhoneNumberConfirmed, " +
                "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES " +
                "('first-user', 'first', {0}, 0, 0, 0, 0, 0), ('second-user', 'second', '{{}}', 0, 0, 0, 0, 0)",
                StoredMetadata);
        }

        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.SafeMigrateAsync();

        using var check = services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();

        foreach (var id in new[] { "first-user", "second-user" })
            (await context.Database.SqlQuery<string>($"SELECT Preferences AS Value FROM AspNetUsers WHERE Id = {id}")
                .SingleAsync()).ShouldBe("{}");
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == "first-user");
        user.Preferences.ShouldBeEmpty();
        user.Metadata["maxStorage"].ShouldBe(5L);
        (await context.Database.SqlQuery<string>($"SELECT Metadata AS Value FROM AspNetUsers WHERE Id = 'first-user'")
            .SingleAsync()).ShouldBe(StoredMetadata);

        var column = await Column(database, "AspNetUsers", "Preferences");
        column.IsNullable.ShouldBe("NO");
        column.Type.ShouldBe("longtext");
        column.CharacterSet.ShouldBe("utf8mb4", "the same character set as Metadata");

        // A user written by the application stores and reads sections
        var written = await context.Users.SingleAsync(u => u.Id == "second-user");
        written.Preferences = new System.Collections.Generic.Dictionary<string, object>
            { ["map"] = new System.Collections.Generic.Dictionary<string, object> { ["zoom"] = 3L } };
        await context.SaveChangesAsync();
        (await context.Database.SqlQuery<string>($"SELECT Preferences AS Value FROM AspNetUsers WHERE Id = 'second-user'")
            .SingleAsync()).ShouldBe("""{"map":{"zoom":3}}""");
    }

    [Test]
    public async Task TheMigration_Reverts_AndTheUserStays()
    {
        var server = await MariaDbServer.GetAsync();
        await using var database = await server.CreateDatabaseAsync("userpreferences_down_identity");
        using var services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Mysql, database.ConnectionString);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUsers (Id, UserName, Metadata, Preferences, EmailConfirmed, PhoneNumberConfirmed, " +
            "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ('kept-user', 'kept', {0}, '{{}}', 0, 0, 0, 0, 0)",
            StoredMetadata);

        await context.GetService<IMigrator>().MigrateAsync(BeforeMigration);

        (await context.Database.SqlQuery<string>($"SELECT Metadata AS Value FROM AspNetUsers WHERE Id = 'kept-user'")
            .SingleAsync()).ShouldBe(StoredMetadata);
        await Should.ThrowAsync<MySqlConnector.MySqlException>(() =>
            context.Database.ExecuteSqlRawAsync("SELECT Preferences FROM AspNetUsers"));
    }

    private sealed record ColumnInfo(string IsNullable, string Type, string? CharacterSet);

    private static async Task<ColumnInfo> Column(MariaDbServer.TestDatabase database, string table, string column)
    {
        await using var connection = new MySqlConnector.MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IS_NULLABLE, DATA_TYPE, CHARACTER_SET_NAME FROM information_schema.COLUMNS " +
                              "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"{table}.{column} exists");
        return new ColumnInfo(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
