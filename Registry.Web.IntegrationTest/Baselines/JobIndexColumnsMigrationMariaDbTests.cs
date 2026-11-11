using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// On the MariaDB production runs (<see cref="MariaDbServer"/>, <c>mariadb:10.6</c>, databases created with
/// <c>latin1_general_ci</c>): the same checks as <c>JobIndexColumnsMigrationTests</c> (Registry.Web.UnitTest) on
/// SQLite. Here the text columns must be <c>utf8mb4</c> although the database is not, and the waiting flag a
/// <c>tinyint(1)</c> with the default <c>0</c>.
/// Set <c>REGISTRY_TEST_MYSQL_CONNECTION</c> to a root connection string to run it against another server (MySQL 8).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~JobIndexColumnsMigrationMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class JobIndexColumnsMigrationMariaDbTests
{
    private const string LegacyJobId = "job-before-the-migration";

    /// <summary>The columns of the migration, with their MySQL type.</summary>
    private static readonly Dictionary<string, string> Columns = new()
    {
        ["ErrorMessage"] = "varchar(2048)", ["OutputPathsJson"] = "longtext", ["TargetLabel"] = "varchar(256)",
        ["WaitingForNode"] = "tinyint(1)", ["CancelledBy"] = "varchar(128)", ["WriteRecordJson"] = "longtext"
    };

    private MariaDbServer _server = null!;

    [SetUp]
    public async Task SetUp() => _server = await MariaDbServer.GetAsync();

    private static RegistryContext Context(ServiceProvider services) =>
        services.CreateScope().ServiceProvider.GetRequiredService<RegistryContext>();

    private static string ColumnsMigration(DbContext context) =>
        context.Database.GetMigrations().Single(m => m.EndsWith("_AddTaskRecordColumns", StringComparison.Ordinal));

    private static string PreviousMigration(DbContext context) =>
        context.Database.GetMigrations().TakeWhile(m => m != ColumnsMigration(context)).Last();

    /// <summary>Migrates an empty database to the migration before the columns and stores a task with the old schema.</summary>
    private static async Task SeedLegacyJobAsync(RegistryContext context)
    {
        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration(context));
        await context.Database.ExecuteSqlRawAsync(
            $"INSERT INTO JobIndices (JobId, OrgSlug, DsSlug, CurrentState, CreatedAtUtc, ToolId, ToolVersion) VALUES ('{LegacyJobId}', 'org', 'ds', 'Deleted', '2026-01-05 00:00:00', 'build', '1')");
    }

    private static async Task<List<ColumnRow>> JobIndexColumnsAsync(RegistryContext context) =>
        await context.Database
            .SqlQueryRaw<ColumnRow>(
                "SELECT COLUMN_NAME AS Name, COLUMN_TYPE AS Type, COLUMN_DEFAULT AS DefaultValue, CHARACTER_SET_NAME AS CharSet " +
                "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'JobIndices'")
            .ToListAsync();

    /// <summary>A task written through the model keeps every new value, text outside latin1 included.</summary>
    private static async Task AssertTheColumnsRoundTripAsync(Func<RegistryContext> context)
    {
        var jobId = $"job-{Guid.NewGuid():N}";
        var message = "Échec: 東京 " + new string('m', 2048 - "Échec: 東京 ".Length);
        await using (var write = context())
        {
            write.JobIndices.Add(new JobIndex
            {
                JobId = jobId, OrgSlug = "org", DsSlug = "ds", CreatedAtUtc = DateTime.UtcNow,
                ErrorMessage = message, OutputPathsJson = "[\"a/東京.tif\"]", TargetLabel = "東京.tif",
                WaitingForNode = true, CancelledBy = "user-id", WriteRecordJson = "{\"paths\":[]}"
            });
            await write.SaveChangesAsync();
        }

        await using var read = context();
        var job = await read.JobIndices.AsNoTracking().SingleAsync(j => j.JobId == jobId);
        job.ErrorMessage.ShouldBe(message);
        job.OutputPathsJson.ShouldBe("[\"a/東京.tif\"]");
        job.TargetLabel.ShouldBe("東京.tif");
        job.WaitingForNode.ShouldBeTrue();
        job.CancelledBy.ShouldBe("user-id");
        job.WriteRecordJson.ShouldBe("{\"paths\":[]}");
    }

    [Test]
    public async Task Up_AddsTheColumns_AndATaskStoredBeforeItReadsNullAndNotWaiting()
    {
        await using var database = await _server.CreateDatabaseAsync("jobindexcolumnsmariadb_up");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);
        await SeedLegacyJobAsync(context);

        await context.GetService<IMigrator>().MigrateAsync();

        var columns = (await JobIndexColumnsAsync(context)).ToDictionary(c => c.Name);
        foreach (var (name, type) in Columns)
        {
            columns.ShouldContainKey(name);
            columns[name].Type.ShouldBe(type, name);
            if (name != "WaitingForNode")
                columns[name].CharSet.ShouldBe("utf8mb4", name);
        }
        columns["WaitingForNode"].DefaultValue.ShouldBe("0");

        var legacy = await context.JobIndices.AsNoTracking().SingleAsync(j => j.JobId == LegacyJobId);
        legacy.ErrorMessage.ShouldBeNull();
        legacy.OutputPathsJson.ShouldBeNull();
        legacy.TargetLabel.ShouldBeNull();
        legacy.WaitingForNode.ShouldBeFalse();
        legacy.CancelledBy.ShouldBeNull();
        legacy.WriteRecordJson.ShouldBeNull();
        legacy.CurrentState.ShouldBe("Deleted");

        await AssertTheColumnsRoundTripAsync(() => Context(services));
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Test]
    public async Task Down_DropsTheColumns_AndUpRunsAgain()
    {
        await using var database = await _server.CreateDatabaseAsync("jobindexcolumnsmariadb_down");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);
        await SeedLegacyJobAsync(context);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();

        await migrator.MigrateAsync(PreviousMigration(context));
        var names = (await JobIndexColumnsAsync(context)).Select(c => c.Name).ToList();
        names.ShouldNotContain(n => Columns.ContainsKey(n));
        names.ShouldContain("ErrorType");
        (await context.Database.SqlQueryRaw<string>("SELECT JobId AS Value FROM JobIndices").ToListAsync())
            .ShouldBe([LegacyJobId], "Down keeps the rows");

        await migrator.MigrateAsync();
        Columns.Keys.ShouldBeSubsetOf((await JobIndexColumnsAsync(context)).Select(c => c.Name));
        await AssertTheColumnsRoundTripAsync(() => Context(services));
    }

    public sealed record ColumnRow(string Name, string Type, string? DefaultValue, string? CharSet);
}
