#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// The RQ-02 items that need a real MySQL server, on the shared MariaDB test server
/// (<see cref="MariaDbServer"/>, the production image), each test in its own database:
/// <list type="bullet">
/// <item>item 4c, <c>SafeMigrateAsync</c> on MySQL (non-transactional DDL): a database with the
/// baseline tables and no <c>__EFMigrationsHistory</c> gets every migration in one run, for both
/// contexts (<see cref="Rq02SafeMigrateTests"/> is the SQLite counterpart);</item>
/// <item>item 2, the live count of version-probe connections: one per connection string per
/// registration for the life of the process, however many scopes build their options.</item>
/// </list>
/// Run: dotnet test Registry.Web.Test --filter "FullyQualifiedName~Rq02MySqlLiveTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("ImprovementBaseline")]
public class Rq02MySqlLiveTests
{
    private MariaDbServer _server = null!;

    [SetUp]
    public async Task SetUp() => _server = await MariaDbServer.GetAsync();

    [Test]
    public Task SafeMigrate_LegacyRegistryDatabaseWithoutHistory_IsBroughtToTheLatestMigration() =>
        AssertSafeMigrateBringsLegacyDatabaseToLatest<RegistryContext>();

    [Test]
    public Task SafeMigrate_LegacyIdentityDatabaseWithoutHistory_IsBroughtToTheLatestMigration() =>
        AssertSafeMigrateBringsLegacyDatabaseToLatest<ApplicationDbContext>();

    private async Task AssertSafeMigrateBringsLegacyDatabaseToLatest<TContext>() where TContext : DbContext
    {
        await using var database = await _server.CreateDatabaseAsync("rq02_safe");
        using var services = HostDbContexts.Build<TContext>(DbProvider.Mysql, database.ConnectionString);
        string[] allMigrations;

        // A legacy deployment: the baseline schema, no migrations history
        using (var scope = services.CreateScope())
        {
            var legacy = scope.ServiceProvider.GetRequiredService<TContext>();
            allMigrations = legacy.Database.GetMigrations().ToArray();
            allMigrations.Length.ShouldBeGreaterThan(1, "the scenario needs migrations after the baseline");

            await legacy.GetService<IMigrator>().MigrateAsync(allMigrations[0]);
            await legacy.Database.ExecuteSqlRawAsync($"DROP TABLE `{HistoryRepository.DefaultTableName}`");
        }

        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TContext>().Database.SafeMigrateAsync();

        using (var scope = services.CreateScope())
        {
            var check = scope.ServiceProvider.GetRequiredService<TContext>();
            (await check.Database.GetPendingMigrationsAsync()).ShouldBeEmpty(
                "every migration after the stamped baseline must be applied in the same run");
            (await check.Database.GetAppliedMigrationsAsync()).ShouldBe(allMigrations);
            check.Database.HasPendingModelChanges().ShouldBeFalse();
        }
    }

    /// <summary>
    /// Counts physical connections with the server's <c>Connections</c> status variable. The probed
    /// connection strings disable pooling, so every probe is one physical connection (with pooling a
    /// repeated probe reuses an idle connection: a pool round-trip per scope instead of a connect).
    /// The calibration step shows the counter sees each <c>AutoDetect</c>, the per-scope cost before
    /// the fix; the host registration then costs one probe per context for the whole process.
    /// </summary>
    [Test]
    public async Task VersionProbe_ManyScopesOfBothContexts_OpenOneConnectionPerConnectionString()
    {
        const int scopes = 10;
        await using var data = await _server.CreateDatabaseAsync("rq02_probe_data");
        await using var identity = await _server.CreateDatabaseAsync("rq02_probe_identity");
        var dataConnection = Unpooled(data.ConnectionString);
        var identityConnection = Unpooled(identity.ConnectionString);

        // One monitoring connection, opened before the first reading, so reading costs nothing
        await using var monitor = new MySqlConnection(_server.ConnectionString);
        await monitor.OpenAsync();

        var before = await Connections(monitor);
        for (var i = 0; i < scopes; i++)
            ServerVersion.AutoDetect(dataConnection);
        var perProbe = await Connections(monitor) - before;
        perProbe.ShouldBe(scopes, "calibration: each AutoDetect opens one connection");

        var services = new ServiceCollection()
            .AddHostDbContext<RegistryContext>(DbProvider.Mysql, dataConnection)
            .AddHostDbContext<ApplicationDbContext>(DbProvider.Mysql, identityConnection);
        await using var provider = services.BuildServiceProvider();

        before = await Connections(monitor);
        ResolveInScopes(provider, scopes);
        var first = await Connections(monitor) - before;

        before = await Connections(monitor);
        ResolveInScopes(provider, scopes);
        var later = await Connections(monitor) - before;

        TestContext.Out.WriteLine($"Version-probe connections: {perProbe} for {scopes} AutoDetect calls (before the fix: " +
                                  $"one per context per scope); after: {first} over the first {scopes} scopes of both " +
                                  $"contexts, {later} over the next {scopes}");
        first.ShouldBe(2, "one probe per connection string (one per context) for the first scope");
        later.ShouldBe(0, "later scopes reuse the resolved version");
    }

    private static void ResolveInScopes(IServiceProvider provider, int scopes)
    {
        for (var i = 0; i < scopes; i++)
        {
            using var scope = provider.CreateScope();
            // Building the options is where the version is resolved; the contexts open nothing here
            _ = scope.ServiceProvider.GetRequiredService<RegistryContext>().Model;
            _ = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Model;
        }
    }

    private static string Unpooled(string connectionString) =>
        new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task<long> Connections(MySqlConnection monitor)
    {
        await using var command = monitor.CreateCommand();
        command.CommandText = "SHOW GLOBAL STATUS LIKE 'Connections'";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return long.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
    }
}
