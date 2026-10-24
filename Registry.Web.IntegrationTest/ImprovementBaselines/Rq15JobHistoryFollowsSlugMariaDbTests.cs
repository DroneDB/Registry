#nullable enable
using System;
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
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-15: the job history of a dataset follows it when it is renamed or moved to another organization, on the MariaDB
/// production runs (<see cref="MariaDbServer"/>: the retrying execution strategy of MySQL, the case-insensitive
/// <c>latin1_general_ci</c> collation, the real unique index of the dataset slugs). A row of another case ("SOURCE",
/// "MOVING": the collation matches it in SQL) is not that dataset's and stays. The same scenarios run on SQLite in
/// <c>Rq15JobHistoryFollowsSlugTests</c> (Registry.Web.UnitTest).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~Rq15JobHistoryFollowsSlugMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class Rq15JobHistoryFollowsSlugMariaDbTests
{
    [Test]
    public async Task Rename_TheJobsOfTheDataset_FollowItsSlug()
    {
        await using var database = await CreateAsync("rq15_hist_rename");

        await TestDatasets.AdminManager(database.Context).Rename("source", "moving", "renamed");

        Jobs(database.Check()).ShouldBe(
        [
            ("a", "source", "renamed"), ("b", "source", "renamed"), ("c", "source", "staying"),
            ("d", "other", "moving"), ("orphan", "SOURCE", "MOVING")
        ]);
    }

    [Test]
    public async Task Move_TheJobsOfTheDataset_FollowItToTheOtherOrganizationAndItsNewSlug()
    {
        await using var database = await CreateAsync("rq15_hist_move");

        var results = (await TestDatasets.AdminManager(database.Context)
            .MoveToOrganization("source", ["moving"], "other", ConflictResolutionStrategy.Rename)).ToArray();

        results.ShouldHaveSingleItem().Success.ShouldBeTrue(results[0].Error);
        results[0].NewSlug.ShouldBe("moving_1");
        Jobs(database.Check()).ShouldBe(
        [
            ("a", "other", "moving_1"), ("b", "other", "moving_1"), ("c", "source", "staying"),
            ("d", "other", "moving"), ("orphan", "SOURCE", "MOVING")
        ]);
    }

    private static (string Job, string Org, string Dataset)[] Jobs(RegistryContext context) =>
        context.JobIndices.AsNoTracking().ToArray().OrderBy(j => j.JobId, StringComparer.Ordinal)
            .Select(j => (j.JobId, j.OrgSlug, j.DsSlug)).ToArray();

    // "source" has "moving" and "staying"; "other" has a "moving" of its own (the one that makes the move rename)
    private static async Task<Scope> CreateAsync(string name)
    {
        var server = await MariaDbServer.GetAsync();
        var database = await server.CreateDatabaseAsync(name);
        var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        await context.GetService<IMigrator>().MigrateAsync();

        var source = Seed.Organization(context, "source", ownerId: "admin-id");
        Seed.Dataset(context, source, "moving");
        Seed.Dataset(context, source, "staying");
        Seed.Dataset(context, Seed.Organization(context, "other", ownerId: "admin-id"), "moving");
        context.JobIndices.AddRange(Job("a", "source", "moving"), Job("b", "source", "moving"),
            Job("c", "source", "staying"), Job("d", "other", "moving"), Job("orphan", "SOURCE", "MOVING"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return new Scope(database, services, scope, context);
    }

    private static JobIndex Job(string id, string org, string dataset) => new()
    {
        JobId = id, OrgSlug = org, DsSlug = dataset, CurrentState = "Succeeded", CreatedAtUtc = DateTime.UtcNow
    };

    /// <summary>The database, the context a test works with, and a fresh one for reading what was saved.</summary>
    private sealed class Scope(IAsyncDisposable database, ServiceProvider services, AsyncServiceScope scope,
        RegistryContext context) : IAsyncDisposable
    {
        public RegistryContext Context { get; } = context;

        public RegistryContext Check() =>
            services.CreateAsyncScope().ServiceProvider.GetRequiredService<RegistryContext>();

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await services.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
