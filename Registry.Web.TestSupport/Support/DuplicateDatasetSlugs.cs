using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Registry.Web.Data;
using Shouldly;

namespace Registry.Web.Test.Support;

/// <summary>
/// A registry database at the migration before the unique <c>(OrganizationSlug, Slug)</c> index of
/// <c>Datasets</c>, holding datasets that share a slug, and what the migration must make of it. One scenario for every
/// provider (the tests run it on SQLite and on the MariaDB of production): the same rows go in, the same rows come out,
/// except where the engine compares text differently (a case-insensitive collation sees <c>Survey</c> and <c>survey</c>
/// as one slug, SQLite's default does not).
/// <para>
/// The rows are written with SQL of the old schema, because the model of the current code has the new index: an
/// <c>INSERT</c> through the context could not create the duplicates a production database holds.
/// </para>
/// </summary>
#pragma warning disable EF1002 // the SQL is built from the constants of this scenario, never from input
public static class DuplicateDatasetSlugs
{
    /// <summary>The slug of the three copies in <see cref="Alpha"/>.</summary>
    public const string Survey = "survey";

    public const string Alpha = "alpha";
    public const string Beta = "beta";
    public const string Gamma = "gamma";

    /// <summary>The migration the tests start from: the last one before the unique index.</summary>
    public static string PreviousMigration(DbContext context) =>
        context.Database.GetMigrations().TakeWhile(m => m != IndexMigration(context)).Last();

    /// <summary>The migration that adds the unique index and repairs the data (by name: later migrations follow it).</summary>
    public static string IndexMigration(DbContext context) =>
        context.Database.GetMigrations().Single(m => m.EndsWith("_DatasetSlugUnique", StringComparison.Ordinal));

    /// <summary>Migrates an empty database to <see cref="PreviousMigration"/> and writes the rows.</summary>
    public static async Task SeedAsync(RegistryContext context)
    {
        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration(context));

        foreach (var org in new[] { Alpha, Beta, Gamma })
            await Execute(context, $"INSERT INTO Organizations (Slug, Name, CreationDate, IsPublic) VALUES ('{org}', '{org}', '2024-01-01 00:00:00', 1)");

        // (Id, organization, slug)
        foreach (var (id, org, slug) in Rows)
            await Execute(context,
                $"INSERT INTO Datasets (Id, CreationDate, InternalRef, OrganizationSlug, Slug) VALUES ({id}, '2024-01-0{id % 9 + 1} 00:00:00', '00000000-0000-0000-0000-{id:D12}', '{org}', '{slug}')");

        foreach (var (jobId, org, slug) in Jobs)
            await Execute(context,
                $"INSERT INTO JobIndices (JobId, OrgSlug, DsSlug, CurrentState, CreatedAtUtc, ToolId, ToolVersion) VALUES ('{jobId}', '{org}', '{slug}', 'Succeeded', '2024-01-05 00:00:00', 'build', '1')");
    }

    // The jobs name their dataset by slug, so those of a slug that three datasets shared (alpha/survey) cannot say which
    // one they ran on
    private static readonly (string JobId, string Org, string Slug)[] Jobs =
    [
        ("job-alpha-survey", Alpha, Survey), ("job-beta-survey", Beta, Survey), ("job-gamma-x", Gamma, "x")
    ];

    private static readonly (int Id, string Org, string Slug)[] Rows =
    [
        // Three copies of one slug in one organization: the first keeps it
        (1, Alpha, Survey), (2, Alpha, Survey), (3, Alpha, Survey),
        // The same slug in another organization is not a duplicate
        (4, Beta, Survey),
        // Different slugs are not duplicates
        (5, Beta, "orthophoto"), (6, Beta, "dsm"),
        // Case: one slug for a case-insensitive collation (MariaDB), two for SQLite's default
        (7, Gamma, "Plot"), (8, Gamma, "plot"),
        // A copy whose repaired name is taken by another dataset: the second pass renames it again
        (9, Gamma, "x"), (10, Gamma, "x"), (11, Gamma, "x_10"),
        // A long slug: the repaired name still fits the 128 characters of the column
        (12, Gamma, new string('l', 128)), (13, Gamma, new string('l', 128))
    ];

    /// <summary>The slugs by id after the migration, for an engine that compares slugs <paramref name="caseInsensitive"/>ly.</summary>
    public static IReadOnlyDictionary<int, string> Expected(bool caseInsensitive) => new Dictionary<int, string>
    {
        [1] = Survey, [2] = "survey_2", [3] = "survey_3",
        [4] = Survey,
        [5] = "orthophoto", [6] = "dsm",
        [7] = "Plot", [8] = caseInsensitive ? "plot_8" : "plot",
        [9] = "x", [10] = "x_10_10", [11] = "x_10",
        [12] = new string('l', 128), [13] = new string('l', 128 - "_13".Length) + "_13"
    };

    /// <summary>What the migration did: every row is there, repaired as in <see cref="Expected"/>, nothing else changed.</summary>
    public static async Task AssertRepairedAsync(RegistryContext context, bool caseInsensitive)
    {
        var rows = await context.Database
            .SqlQueryRaw<SlugRow>("SELECT Id AS Id, Slug AS Slug, OrganizationSlug AS OrganizationSlug FROM Datasets ORDER BY Id")
            .ToListAsync();

        rows.Select(r => r.Id).ShouldBe(Rows.Select(r => r.Id), "no dataset is deleted");
        rows.ToDictionary(r => r.Id, r => r.Slug).ShouldBe(Expected(caseInsensitive));
        rows.ToDictionary(r => r.Id, r => r.OrganizationSlug)
            .ShouldBe(Rows.ToDictionary(r => r.Id, r => r.Org), "only slugs change");

        // Not rewritten: nothing in a job row or in the database tells which of the copies it ran on (the duplicates
        // were as ambiguous for every lookup before the migration), so the jobs stay with the slug, which the first
        // dataset keeps. The slug repair of /sys/fix-invalid-slugs, which renames one known dataset, does rewrite them.
        (await context.Database.SqlQueryRaw<JobRow>("SELECT JobId AS JobId, OrgSlug AS OrgSlug, DsSlug AS DsSlug FROM JobIndices ORDER BY JobId")
                .ToListAsync())
            .Select(j => (j.JobId, j.OrgSlug, j.DsSlug)).ShouldBe(Jobs.OrderBy(j => j.JobId, StringComparer.Ordinal));
    }

    public static async Task<List<string>> IndexNamesAsync(RegistryContext context, string table) =>
        context.Database.IsSqlite()
            ? await context.Database.SqlQueryRaw<string>(
                    $"SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = '{table}' AND name NOT LIKE 'sqlite_%'")
                .ToListAsync()
            : await context.Database.SqlQueryRaw<string>(
                    $"SELECT DISTINCT INDEX_NAME AS Value FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'")
                .ToListAsync();

    /// <summary>The unique index exists and refuses a second dataset with the slug of an existing one.</summary>
    public static async Task AssertUniqueIndexRefusesADuplicateAsync(RegistryContext context)
    {
        (await IndexNamesAsync(context, "Datasets")).ShouldContain("IX_Datasets_OrganizationSlug_Slug");

        var duplicate = () => Execute(context,
            $"INSERT INTO Datasets (Id, CreationDate, InternalRef, OrganizationSlug, Slug) VALUES (100, '2024-02-01 00:00:00', '00000000-0000-0000-0000-000000000100', '{Alpha}', '{Survey}')");
        await duplicate.ShouldThrowAsync<Exception>("the database keeps slugs unique, not only the application");

        // Another organization may use the slug
        await Execute(context,
            $"INSERT INTO Datasets (Id, CreationDate, InternalRef, OrganizationSlug, Slug) VALUES (101, '2024-02-01 00:00:00', '00000000-0000-0000-0000-000000000101', '{Gamma}', '{Survey}')");
    }

    public static Task Execute(DbContext context, string sql) => context.Database.ExecuteSqlRawAsync(sql);

    public sealed record SlugRow(int Id, string Slug, string OrganizationSlug);

    public sealed record JobRow(string JobId, string OrgSlug, string DsSlug);
}
