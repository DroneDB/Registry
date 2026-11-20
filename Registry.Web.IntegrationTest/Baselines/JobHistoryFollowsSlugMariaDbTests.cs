using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Datasets;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The job history of a dataset follows it when it is renamed or moved to another organization, on the MariaDB
/// production runs (<see cref="MariaDbServer"/>: the retrying execution strategy of MySQL, the case-insensitive
/// <c>latin1_general_ci</c> collation, the real unique index of the dataset slugs). A row of another case ("SOURCE",
/// "MOVING": the collation matches it in SQL) is not that dataset's and stays. The same scenarios run on SQLite in
/// <c>JobHistoryFollowsSlugTests</c> (Registry.Web.UnitTest).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~JobHistoryFollowsSlugMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class JobHistoryFollowsSlugMariaDbTests
{
    [Test]
    public async Task Rename_TheJobsOfTheDataset_FollowItsSlug()
    {
        await using var database = await CreateAsync("jobhistoryfollowsslugmariadb_hist_rename");

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
        await using var database = await CreateAsync("jobhistoryfollowsslugmariadb_hist_move");

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

    /// <summary>
    /// A dataset with a job queued or running is not renamed (the rule of the slug repair); the row of another case
    /// ("SOURCE", "MOVING": the collation matches it in SQL) is another dataset's and does not block.
    /// </summary>
    [Test]
    public async Task Rename_WithAJobRunning_IsRefused_ButNotForTheRowOfAnotherCase()
    {
        await using var database = await CreateAsync("jobhistoryfollowsslugmariadb_hist_busy");
        var manager = TestDatasets.AdminManager(database.Context);
        database.Context.JobIndices.Add(Job("running", "source", "moving", "Processing"));
        await database.Context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ConflictException>(() => manager.Rename("source", "moving", "renamed"));
        ex.Message.ShouldBe(DatasetJobsRule.Message("source", "moving"));
        database.Context.ChangeTracker.Clear();

        database.Context.JobIndices.Remove(await database.Context.JobIndices.SingleAsync(j => j.JobId == "running"));
        database.Context.JobIndices.Add(Job("orphan-running", "SOURCE", "MOVING", "Processing"));
        await database.Context.SaveChangesAsync();

        await manager.Rename("source", "moving", "renamed");

        Jobs(database.Check()).ShouldContain(("a", "source", "renamed"));
    }

    /// <summary>A is deleted, B takes its slug before the cleanup runs: the cleanup leaves B's history and jobs alone.</summary>
    [Test]
    public async Task ADeletedDatasetsCleanup_LeavesTheDatasetThatTookItsSlug_Alone()
    {
        await using var database = await CreateAsync("jobhistoryfollowsslugmariadb_hist_deleted");
        var queue = new CleanupJobRecorder();
        var manager = TestDatasets.AdminManager(database.Context, queue.Jobs.Object);
        database.Context.JobIndices.Add(Job("moving-running", "source", "moving", "Processing"));
        await database.Context.SaveChangesAsync();

        await manager.Delete("source", "moving");
        await manager.Rename("source", "staying", "moving");
        database.Context.JobIndices.Add(Job("new", "source", "moving", "Processing"));
        await database.Context.SaveChangesAsync();

        var ddb = new Moq.Mock<Registry.Ports.IDdbManager>();
        await queue.RunAsync(new DatasetCleanupService(database.Context, ddb.Object, queue.Jobs.Object,
            new JobIndexQuery(database.Context), NullLogger<DatasetCleanupService>.Instance));

        Jobs(database.Check()).ShouldBe(
        [
            ("c", "source", "moving"), ("new", "source", "moving"), ("d", "other", "moving"), ("orphan", "SOURCE", "MOVING")
        ], ignoreOrder: true);
        queue.Jobs.Verify(j => j.Delete("moving-running"), Moq.Times.Once);
        queue.Jobs.Verify(j => j.Delete("new"), Moq.Times.Never);
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

    private static JobIndex Job(string id, string org, string dataset, string state = "Succeeded") => new()
    {
        JobId = id, OrgSlug = org, DsSlug = dataset, CurrentState = state, CreatedAtUtc = DateTime.UtcNow
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
