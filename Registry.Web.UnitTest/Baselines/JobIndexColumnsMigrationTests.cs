using System;
using System.Collections.Generic;
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
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// On SQLite: the migration of the task record columns of <c>JobIndices</c> (ErrorMessage, OutputPathsJson,
/// TargetLabel, WaitingForNode, CancelledBy, WriteRecordJson) adds them with their types, a task stored before it
/// reads no message, no paths, no label, not waiting, not cancelled by a user and no write record, and the migration
/// can be undone. The MariaDB counterpart is <c>JobIndexColumnsMigrationMariaDbTests</c> (Registry.Web.IntegrationTest).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~JobIndexColumnsMigrationTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class JobIndexColumnsMigrationTests
{
    private const string LegacyJobId = "job-before-the-migration";

    /// <summary>The columns of the migration, with their SQLite type.</summary>
    private static readonly Dictionary<string, string> Columns = new()
    {
        ["ErrorMessage"] = "TEXT", ["OutputPathsJson"] = "TEXT", ["TargetLabel"] = "TEXT",
        ["WaitingForNode"] = "INTEGER", ["CancelledBy"] = "TEXT", ["WriteRecordJson"] = "TEXT"
    };

    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("jobindexcolumnsmigration-").FullName;

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private ServiceProvider Services() =>
        HostDbContexts.Build<RegistryContext>(DbProvider.Sqlite,
            $"Data Source={Path.Combine(_root, "jobindex.db")};Mode=ReadWriteCreate");

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
            .SqlQueryRaw<ColumnRow>("SELECT name AS Name, type AS Type, dflt_value AS DefaultValue FROM pragma_table_info('JobIndices')")
            .ToListAsync();

    /// <summary>A task written through the model keeps every new value.</summary>
    private static async Task AssertTheColumnsRoundTripAsync(Func<RegistryContext> context)
    {
        var jobId = $"job-{Guid.NewGuid():N}";
        var message = new string('m', 2048);
        await using (var write = context())
        {
            write.JobIndices.Add(new JobIndex
            {
                JobId = jobId, OrgSlug = "org", DsSlug = "ds", CreatedAtUtc = DateTime.UtcNow,
                ErrorMessage = message, OutputPathsJson = "[\"a/b.tif\"]", TargetLabel = "b.tif",
                WaitingForNode = true, CancelledBy = "user-id", WriteRecordJson = "{\"paths\":[]}"
            });
            await write.SaveChangesAsync();
        }

        await using var read = context();
        var job = await read.JobIndices.AsNoTracking().SingleAsync(j => j.JobId == jobId);
        job.ErrorMessage.ShouldBe(message);
        job.OutputPathsJson.ShouldBe("[\"a/b.tif\"]");
        job.TargetLabel.ShouldBe("b.tif");
        job.WaitingForNode.ShouldBeTrue();
        job.CancelledBy.ShouldBe("user-id");
        job.WriteRecordJson.ShouldBe("{\"paths\":[]}");
    }

    [Test]
    public async Task Up_AddsTheColumns_AndATaskStoredBeforeItReadsNullAndNotWaiting()
    {
        await using var services = Services();
        using var context = Context(services);
        await SeedLegacyJobAsync(context);

        await context.GetService<IMigrator>().MigrateAsync();

        var columns = (await JobIndexColumnsAsync(context)).ToDictionary(c => c.Name);
        foreach (var (name, type) in Columns)
        {
            columns.ShouldContainKey(name);
            columns[name].Type.ShouldBe(type, name);
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
        await using var services = Services();
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

    public sealed record ColumnRow(string Name, string Type, string? DefaultValue);
}
