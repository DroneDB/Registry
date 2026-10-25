#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;
using Registry.Web.Data;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Defect found by <c>PendingModelChangesTests</c> (Registry.Web.UnitTest): <c>AspNetUsers.Metadata</c> is NOT NULL
/// in the model, but older schemas let it be NULL, and NULL or '' read back as a null dictionary
/// (which <c>UsersManager</c> dereferences). Both are repaired to <c>{}</c>, what the application writes
/// for empty metadata (the serialized empty dictionary <see cref="User.Metadata"/> starts as):
/// <list type="bullet">
/// <item>MySQL: the 2021 InitialCreate had the column nullable. <c>UserMetadataRequired</c> repairs the
/// data, then makes the column NOT NULL. Run on the production MariaDB image (<see cref="MariaDbServer"/>).</item>
/// <item>SQLite: identity databases created from 2020-07 to 2022-05 by <c>EnsureCreated</c> have the
/// column nullable and no migrations history; <c>SafeMigrateAsync</c> stamps them at InitialCreate
/// without touching the column. <c>RepairUserMetadata</c> repairs the data (no schema change).</item>
/// </list>
/// Each runs through <see cref="EfCoreExtenders.SafeMigrateAsync"/>, as <c>DatabaseInitializer</c> does.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~IdentityMetadataRepairTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class IdentityMetadataRepairTests
{
    private const string MySqlInitialCreate = "20210107165828_InitialCreate";
    private const string SqliteInitialCreate = "20220527000753_InitialCreate";
    private const string EmptyMetadata = "{}";
    private const string StoredMetadata = "{\"maxStorage\":5}";

    [Test]
    public void EmptyMetadata_IsWhatTheApplicationWritesForANewUser() =>
        JsonConvert.SerializeObject(new User().Metadata).ShouldBe(EmptyMetadata);

    [Test]
    public async Task MySql_UserMetadataRequired_RepairsNullAndEmptyMetadataAndMakesTheColumnNotNull()
    {
        var server = await MariaDbServer.GetAsync();
        await using var database = await server.CreateDatabaseAsync("identitymetadatarepair_identity");
        using var services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Mysql, database.ConnectionString);

        // A deployment still at the 2021 schema
        using (var scope = services.CreateScope())
        {
            var legacy = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await legacy.GetService<IMigrator>().MigrateAsync(MySqlInitialCreate);
            (await Column(database, "AspNetUsers", "Metadata")).IsNullable.ShouldBe("YES");
            await InsertLegacyUsers(legacy);
        }

        await SafeMigrateAndAssertRepaired(services);

        var metadata = await Column(database, "AspNetUsers", "Metadata");
        metadata.IsNullable.ShouldBe("NO");
        metadata.Type.ShouldBe("longtext");
        metadata.CharacterSet.ShouldBe("utf8mb4", "the column keeps the character set InitialCreate gave it");
        // The claims Id annotation is a snapshot-only correction: the columns stay AUTO_INCREMENT
        (await Column(database, "AspNetUserClaims", "Id")).Extra.ShouldContain("auto_increment");
        (await Column(database, "AspNetRoleClaims", "Id")).Extra.ShouldContain("auto_increment");
    }

    [Test]
    public async Task Sqlite_RepairUserMetadata_RepairsNullAndEmptyMetadataOfALegacyEnsureCreatedDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "identitymetadatarepair-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "identity.db"), Pooling = false
            }.ToString();
            using var services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Sqlite, connectionString);

            using (var scope = services.CreateScope())
            {
                var legacy = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await CreateLegacyEnsureCreatedSchema(legacy);
                await InsertLegacyUsers(legacy);
            }

            await SafeMigrateAndAssertRepaired(services);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The schema a 2020-07..2022-05 <c>EnsureCreated</c> left: today's tables with a nullable Metadata
    /// column and no migrations history. Built from InitialCreate's own DDL, changed only there.
    /// </summary>
    private static async Task CreateLegacyEnsureCreatedSchema(ApplicationDbContext legacy)
    {
        await legacy.GetService<IMigrator>().MigrateAsync(SqliteInitialCreate);

        var db = legacy.Database;
        var table = await db.SqlQueryRaw<string>(
            "SELECT sql AS Value FROM sqlite_master WHERE type = 'table' AND name = 'AspNetUsers'").SingleAsync();
        var indexes = await db.SqlQueryRaw<string>(
            "SELECT sql AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'AspNetUsers' AND sql IS NOT NULL")
            .ToListAsync();
        const string required = "\"Metadata\" TEXT NOT NULL";
        table.ShouldContain(required);

        await db.OpenConnectionAsync();
        try
        {
            // The table is empty, and its dependants are rebuilt against the same name
            await db.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            await db.ExecuteSqlRawAsync("DROP TABLE \"AspNetUsers\"");
            await db.ExecuteSqlRawAsync(table.Replace(required, "\"Metadata\" TEXT NULL", StringComparison.Ordinal));
            foreach (var index in indexes)
                await db.ExecuteSqlRawAsync(index);
            await db.ExecuteSqlRawAsync($"DROP TABLE \"{HistoryRepository.DefaultTableName}\"");
            await db.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }
        finally
        {
            await db.CloseConnectionAsync();
        }
    }

    /// <summary>A NULL, an empty and a stored metadata row (portable SQL: MySQL and SQLite).</summary>
    private static async Task InsertLegacyUsers(ApplicationDbContext legacy) =>
        await legacy.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUsers (Id, UserName, Metadata, EmailConfirmed, PhoneNumberConfirmed, " +
            "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES " +
            "('null-user', 'legacy', NULL, 0, 0, 0, 0, 0), ('empty-user', 'empty', '', 0, 0, 0, 0, 0), " +
            "('stored-user', 'stored', {0}, 0, 0, 0, 0, 0)",
            StoredMetadata);

    private static async Task SafeMigrateAndAssertRepaired(ServiceProvider services)
    {
        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.SafeMigrateAsync();

        using var check = services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await context.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();

        async Task<string?> Stored(string id) => await context.Database
            .SqlQuery<string>($"SELECT Metadata AS Value FROM AspNetUsers WHERE Id = {id}").SingleAsync();
        (await Stored("null-user")).ShouldBe(EmptyMetadata);
        (await Stored("empty-user")).ShouldBe(EmptyMetadata);
        (await Stored("stored-user")).ShouldBe(StoredMetadata);

        var users = await context.Users.AsNoTracking().ToDictionaryAsync(u => u.Id);
        foreach (var repaired in new[] { "null-user", "empty-user" })
        {
            users[repaired].Metadata.ShouldNotBeNull($"{repaired} reads back as metadata");
            users[repaired].Metadata.ShouldBeEmpty();
        }
        users["stored-user"].Metadata.ShouldContainKey("maxStorage");
    }

    private sealed record ColumnInfo(string IsNullable, string Type, string? CharacterSet, string Extra);

    private static async Task<ColumnInfo> Column(MariaDbServer.TestDatabase database, string table, string column)
    {
        await using var connection = new MySqlConnector.MySqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IS_NULLABLE, DATA_TYPE, CHARACTER_SET_NAME, EXTRA FROM information_schema.COLUMNS " +
                              "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column";
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@column", column);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"{table}.{column} exists");
        return new ColumnInfo(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3));
    }
}
