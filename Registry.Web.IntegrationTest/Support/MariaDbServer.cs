#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Configurations;
using MySqlConnector;
using NUnit.Framework;
using Testcontainers.MariaDb;

namespace Registry.Web.Test.Support;

/// <summary>
/// One MariaDB server for the whole test run: a Testcontainers container of the image production
/// runs (<c>docker/production/docker-compose.yml</c>), started on first use and stopped by
/// <see cref="GlobalSetup"/> (Testcontainers' Ryuk removes it if the run dies first). The data
/// directory is a tmpfs, so the container leaves no volume behind.
/// <para>
/// Docker missing is the only reason to skip: it is detected explicitly (no Docker endpoint), and
/// outside CI the calling test is ignored with that reason. In CI, or when Docker is there but the
/// server does not start, the test fails: a MySQL regression must never pass by not running.
/// </para>
/// Tests isolate themselves by table prefix in <see cref="ConnectionString"/> (the Hangfire
/// tests) or in their own database (<see cref="CreateDatabaseAsync"/>).
/// </summary>
public sealed class MariaDbServer
{
    /// <summary>The production image (docker/production/docker-compose.yml).</summary>
    public const string Image = "mariadb:10.6";

    /// <summary>The collation production creates its databases with (docker/production/initialize-template.sql).</summary>
    public const string ProductionCollation = "latin1_general_ci";

    private const string SharedDatabase = "registry_test";

    private static readonly Lazy<Task<MariaDbServer>> Starting = new(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// A server to use instead of starting the container (a root connection string, for example a MySQL 8 the developer
    /// runs): the MySQL-version-sensitive tests can be run against it. Not used by CI.
    /// </summary>
    public const string ServerOverrideVariable = "REGISTRY_TEST_MYSQL_CONNECTION";

    private readonly MariaDbContainer? _container;

    private MariaDbServer(MariaDbContainer? container, string connectionString)
    {
        _container = container;
        ConnectionString = connectionString;
    }

    /// <summary>
    /// Root connection string of the shared database, with the options production's Hangfire
    /// connection uses (<c>Allow User Variables=true</c>).
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// The shared server; ignores the calling test when Docker is unavailable outside CI. A failed
    /// start is not retried: every test that needs the server fails with the same error.
    /// </summary>
    public static Task<MariaDbServer> GetAsync()
    {
        if (TestcontainersSettings.OS.DockerEndpointAuthConfig is null && OverrideConnection is null)
        {
            const string reason = "Docker is not available (no Docker endpoint found): the MariaDB tests need it";
            if (IsCi)
                Assert.Fail(reason + "; CI must run them");
            Assert.Ignore(reason);
        }

        return Starting.Value;
    }

    /// <summary>Stops the server if this run started it.</summary>
    public static async Task StopAsync()
    {
        if (Starting is not { IsValueCreated: true, Value.IsCompletedSuccessfully: true })
            return;

        if (Starting.Value.Result._container is { } container)
            await container.DisposeAsync();
    }

    /// <summary>
    /// A new, empty database with the production collation; dispose it to drop it.
    /// </summary>
    public async Task<TestDatabase> CreateDatabaseAsync(string prefix)
    {
        var name = prefix + "_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync(ConnectionString, $"CREATE DATABASE `{name}` COLLATE '{ProductionCollation}'");
        var builder = new MySqlConnectionStringBuilder(ConnectionString) { Database = name };
        return new TestDatabase(this, name, builder.ConnectionString);
    }

    /// <summary>Runs <paramref name="sql"/> on a new connection to <paramref name="connectionString"/>.</summary>
    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs a scalar query on a new connection to <paramref name="connectionString"/>.</summary>
    public static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    private static string? OverrideConnection => Environment.GetEnvironmentVariable(ServerOverrideVariable) is { Length: > 0 } value ? value : null;

    private static async Task<MariaDbServer> StartAsync()
    {
        if (OverrideConnection is { } existing)
        {
            var overridden = new MySqlConnectionStringBuilder(existing) { AllowUserVariables = true };
            await ExecuteAsync(overridden.ConnectionString, $"CREATE DATABASE IF NOT EXISTS `{SharedDatabase}` COLLATE '{ProductionCollation}'");
            overridden.Database = SharedDatabase;
            TestContext.Progress.WriteLine($"MySQL test server from {ServerOverrideVariable} at {overridden.Server}:{overridden.Port}");
            return new MariaDbServer(null, overridden.ConnectionString);
        }

        var container = new MariaDbBuilder(Image)
            .WithUsername("root")
            .WithDatabase(SharedDatabase)
            .WithTmpfsMount("/var/lib/mysql")
            .Build();
        await container.StartAsync();

        await ExecuteAsync(container.GetConnectionString(),
            $"ALTER DATABASE `{SharedDatabase}` COLLATE '{ProductionCollation}'");
        var builder = new MySqlConnectionStringBuilder(container.GetConnectionString()) { AllowUserVariables = true };
        TestContext.Progress.WriteLine($"MariaDB test server ({Image}) at {builder.Server}:{builder.Port}");
        return new MariaDbServer(container, builder.ConnectionString);
    }

    /// <summary>A database owned by one test; disposing drops it.</summary>
    public sealed class TestDatabase(MariaDbServer server, string name, string connectionString) : IAsyncDisposable
    {
        public string Name { get; } = name;

        public string ConnectionString { get; } = connectionString;

        public ValueTask DisposeAsync() =>
            new(ExecuteAsync(server.ConnectionString, $"DROP DATABASE IF EXISTS `{Name}`"));
    }
}
