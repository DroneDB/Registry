using System;
using System.Linq;
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
/// The MySQL <c>AddUserCreatedDate</c> migration (FR-ADM-19, SPEC §8.3) on the production MariaDB image
/// (<see cref="MariaDbServer"/>): a user that exists before it has no <c>CreatedDate</c> (NULL, the Hub shows
/// "Unknown"), the column is a nullable <c>datetime(6)</c>, a date written through the model reads back in UTC, and the
/// migration reverts (the column goes, the user stays). The SQLite migration runs in
/// <c>UserCreatedDateMigrationTests</c> (Registry.Web.UnitTest).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~UserCreatedDateMigrationMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class UserCreatedDateMigrationMariaDbTests
{
    private static string PreviousMigration(DbContext context)
    {
        var migrations = context.Database.GetMigrations().ToList();
        return migrations[migrations.FindIndex(m => m.EndsWith("_AddUserCreatedDate", StringComparison.Ordinal)) - 1];
    }

    private static Task InsertUserAsync(ApplicationDbContext context, string id) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUsers (Id, UserName, Metadata, Preferences, EmailConfirmed, PhoneNumberConfirmed, " +
            "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ({0}, {0}, '{{}}', '{{}}', 0, 0, 0, 0, 0)", id);

    [Test]
    public async Task AUserThatExistedBeforeTheMigration_HasNoCreatedDate_AndANewDateReadsBackInUtc()
    {
        var server = await MariaDbServer.GetAsync();
        await using var database = await server.CreateDatabaseAsync("usercreateddate_identity");
        using var services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Mysql, database.ConnectionString);

        using (var scope = services.CreateScope())
        {
            var legacy = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await legacy.GetService<IMigrator>().MigrateAsync(PreviousMigration(legacy));
            await InsertUserAsync(legacy, "old-user");
        }

        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.SafeMigrateAsync();

        using var check = services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        (await context.Users.AsNoTracking().SingleAsync(u => u.Id == "old-user")).CreatedDate.ShouldBeNull();

        var column = await Column(database, "AspNetUsers", "CreatedDate");
        column.IsNullable.ShouldBe("YES");
        column.Type.ShouldBe("datetime");

        var created = new DateTime(2026, 11, 9, 10, 20, 30, 123, DateTimeKind.Utc);
        var user = await context.Users.SingleAsync(u => u.Id == "old-user");
        user.CreatedDate = created;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var read = (await context.Users.AsNoTracking().SingleAsync(u => u.Id == "old-user")).CreatedDate;
        read.ShouldBe(created);
        read!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task TheMigration_Reverts_AndTheUserStays()
    {
        var server = await MariaDbServer.GetAsync();
        await using var database = await server.CreateDatabaseAsync("usercreateddate_down_identity");
        using var services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Mysql, database.ConnectionString);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();
        await InsertUserAsync(context, "kept-user");

        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration(context));

        (await context.Database.SqlQuery<string>($"SELECT Id AS Value FROM AspNetUsers").ToListAsync())
            .ShouldBe(["kept-user"]);
        await Should.ThrowAsync<MySqlConnector.MySqlException>(() =>
            context.Database.ExecuteSqlRawAsync("SELECT CreatedDate FROM AspNetUsers"));
    }

    private sealed record ColumnInfo(string IsNullable, string Type);

    private static async Task<ColumnInfo> Column(MariaDbServer.TestDatabase database, string table, string column)
    {
        await using var connection = new MySqlConnector.MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IS_NULLABLE, DATA_TYPE FROM information_schema.COLUMNS " +
                              "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"{table}.{column} exists");
        return new ColumnInfo(reader.GetString(0), reader.GetString(1));
    }
}
