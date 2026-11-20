using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// A build that waits for files keeps one task (<see cref="JobIndexWriter.DeleteEarlierSucceededBuildsAsync"/>), on
/// MariaDB (<see cref="MariaDbServer"/>): its <c>latin1_general_ci</c> collation matches "Site/Model.obj" to
/// "site/model.obj" and "ORG" to "org" in SQL, and those rows are another entry's and another dataset's, so they stay.
/// The same rules run on SQLite in <c>WaitingBuildTasksTests</c> (Registry.Web.UnitTest).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~WaitingBuildTasksMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class WaitingBuildTasksMariaDbTests
{
    private const string Path = "site/model.obj";
    private const string Hash = "model-hash";

    private static readonly DateTime Start = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task TheNewestAttemptStays_AndTheRowsOfAnotherCaseAreNotAttemptsOfTheEntry()
    {
        var server = await MariaDbServer.GetAsync();
        await using var database = await server.CreateDatabaseAsync("waitingbuildtasksmariadb_keep_latest");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        await context.GetService<IMigrator>().MigrateAsync();

        context.JobIndices.AddRange(
            Job("first", 0), Job("second", 1), Job("no-hash", 2, hash: null), Job("newest", 3),
            // Older than every attempt, and not one of them
            Job("path-of-another-case", 0, path: "Site/Model.obj"),
            Job("dataset-of-another-case", 0, ds: "DS"),
            Job("organization-of-another-case", 0, org: "ORG"),
            Job("other-content", 0, hash: "earlier-content"),
            Job("failed", 0, state: "Failed"),
            Job("running", 4, state: "Processing"));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var query = new JobIndexQuery(context);
        (await query.HasSeveralSucceededJobsAsync("org", "ds", HeavyToolIds.Build, Path)).ShouldBeTrue();

        var removed = await new JobIndexWriter(context, NullLogger<JobIndexWriter>.Instance, Mock.Of<IRecordCache>())
            .DeleteEarlierSucceededBuildsAsync("org", "ds", Path, Hash);

        removed.ShouldBe(3);
        context.JobIndices.AsNoTracking().Select(j => j.JobId).ToArray().ShouldBe(
        [
            "newest", "path-of-another-case", "dataset-of-another-case", "organization-of-another-case",
            "other-content", "failed", "running"
        ], ignoreOrder: true);
    }

    private static JobIndex Job(string id, int hour, string state = "Succeeded", string path = Path,
        string? hash = Hash, string org = "org", string ds = "ds") => new()
    {
        JobId = id, OrgSlug = org, DsSlug = ds, Path = path, Hash = hash, ToolId = HeavyToolIds.Build,
        CurrentState = state, CreatedAtUtc = Start.AddHours(hour), LastStateChangeUtc = Start.AddHours(hour)
    };
}
