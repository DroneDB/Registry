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
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// On SQLite: the migration of the build notification outbox creates the table with its unique keys, the outbox works
/// on the migrated database (<see cref="BuildNotificationOutboxChecks"/>), and the migration can be undone. The MariaDB
/// counterpart is <c>BuildNotificationsMigrationMariaDbTests</c> (Registry.Web.IntegrationTest).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildNotificationsMigrationTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class BuildNotificationsMigrationTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buildnotificationsmigration-").FullName;

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private ServiceProvider Services() =>
        HostDbContexts.Build<RegistryContext>(DbProvider.Sqlite,
            $"Data Source={Path.Combine(_root, "outbox.db")};Mode=ReadWriteCreate");

    private static RegistryContext Context(ServiceProvider services) =>
        services.CreateScope().ServiceProvider.GetRequiredService<RegistryContext>();

    [Test]
    public async Task TheMigratedTable_KeepsTheSameNewsWaitingOnce_AndTheDigestClaimWorks()
    {
        await using var services = Services();
        using var context = Context(services);

        await context.GetService<IMigrator>().MigrateAsync();

        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "BuildNotifications")).ShouldBe(
        [
            "IX_BuildNotifications_FinishedAtUtc", "IX_BuildNotifications_PendingKey",
            "IX_BuildNotifications_RequestJobId_Outcome", "IX_BuildNotifications_UserId_SentAtUtc"
        ], ignoreOrder: true);
        await BuildNotificationOutboxChecks.AssertTheOutboxWorksAsync(() => Context(services));
        context.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Test]
    public async Task Down_DropsTheTable_AndUpRunsAgain()
    {
        await using var services = Services();
        using var context = Context(services);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync();
        var migrations = context.Database.GetMigrations().ToList();

        await migrator.MigrateAsync(migrations[^2]);
        (await DuplicateDatasetSlugs.IndexNamesAsync(context, "BuildNotifications")).ShouldBeEmpty();

        await migrator.MigrateAsync();
        await BuildNotificationOutboxChecks.AssertTheOutboxWorksAsync(() => Context(services));
    }
}
