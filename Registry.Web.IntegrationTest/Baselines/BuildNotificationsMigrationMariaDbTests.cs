#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// On the MariaDB production runs (<see cref="MariaDbServer"/>, <c>mariadb:10.6</c>, databases created with
/// <c>latin1_general_ci</c>): the same checks as <c>BuildNotificationsMigrationTests</c> (Registry.Web.UnitTest) on
/// SQLite. Here the unique key on a nullable column must allow any number of sent lines (NULL), and the conditional
/// updates of the digest claim are MySQL's translation.
/// Set <c>REGISTRY_TEST_MYSQL_CONNECTION</c> to a root connection string to run it against another server (MySQL 8).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~BuildNotificationsMigrationMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class BuildNotificationsMigrationMariaDbTests
{
    private MariaDbServer _server = null!;

    [SetUp]
    public async Task SetUp() => _server = await MariaDbServer.GetAsync();

    private static RegistryContext Context(ServiceProvider services) =>
        services.CreateScope().ServiceProvider.GetRequiredService<RegistryContext>();

    [Test]
    public async Task TheMigratedTable_KeepsTheSameNewsWaitingOnce_AndTheDigestClaimWorks()
    {
        await using var database = await _server.CreateDatabaseAsync("buildnotificationsmariadb_up");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);

        await context.GetService<IMigrator>().MigrateAsync();

        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "BuildNotifications")).ShouldContain("IX_BuildNotifications_PendingKey");
        await BuildNotificationOutboxChecks.AssertTheOutboxWorksAsync(() => Context(services));
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Test]
    public async Task Down_DropsTheTable_AndUpRunsAgain()
    {
        await using var database = await _server.CreateDatabaseAsync("buildnotificationsmariadb_down");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        using var context = Context(services);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();

        await migrator.MigrateAsync(context.Database.GetMigrations().ToList()[^2]);
        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "BuildNotifications")).ShouldBeEmpty();

        await migrator.MigrateAsync();
        await BuildNotificationOutboxChecks.AssertTheOutboxWorksAsync(() => Context(services));
    }
}
