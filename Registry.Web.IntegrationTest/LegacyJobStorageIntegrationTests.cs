#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.MySql;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MySqlConnector;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Against the production job storage: jobs of the shape v2.7.1 stored (a serialized
/// <c>Registry.Adapters.DroneDB.DDB</c> argument, static <c>HangfireUtils</c> methods) are written
/// by MySqlStorage itself into MariaDB, as production holds them, then a Hangfire server with the
/// DI activator picks them up and runs them to Succeeded on the new implementation.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Network)]
public sealed class LegacyJobStorageIntegrationTests : IDisposable
{
    private const string Org = "org1";
    private static readonly Guid Ref = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    private string _connection = null!;
    private string _prefix = null!;
    private string _root = null!;
    private string _folder = null!;
    private MySqlStorage _storage = null!;
    private ServiceProvider _provider = null!;
    private Mock<IDdbWrapper> _wrapper = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = (await MariaDbServer.GetAsync()).ConnectionString;
        _prefix = "legacyjobs" + Guid.NewGuid().ToString("N")[..12];
        _root = Path.Combine(Path.GetTempPath(), "legacyjobs-storage-" + Guid.NewGuid().ToString("N"));
        _folder = Path.Combine(_root, Org, Ref.ToString());
        Directory.CreateDirectory(Path.Combine(_folder, IDDB.DatabaseFolderName));

        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
        _storage = new MySqlStorage(_connection, new MySqlStorageOptions
        {
            TransactionIsolationLevel = System.Transactions.IsolationLevel.ReadCommitted,
            QueuePollInterval = TimeSpan.FromMilliseconds(100),
            PrepareSchemaIfNecessary = true,
            TablesPrefix = _prefix
        });

        _wrapper = new Mock<IDdbWrapper>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<JobStorage>(_storage);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton(_wrapper.Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new StorageOptions { DatasetsPath = _root }));
        services.AddScoped<IDdbManager, DdbManager>();
        services.AddSingleton(Mock.Of<IBackgroundJobsProcessor>());
        services.AddSingleton(Mock.Of<IJobIndexQuery>());
        services.AddSingleton(TestJobs.Scheduler());
        services.AddScoped<PendingBuildEnqueuer>();
        services.AddSingleton(TestJobs.Budget());
        services.AddScoped<DdbJobs>();
        services.AddScoped<FileJobs>();
        services.AddScoped<HealthCheckJob>();
        services.AddScoped<ExpiredRecurringJobsSweep>();
        services.AddScoped<HangfireUtils>();
        _provider = services.BuildServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        _provider.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);

        await using var connection = new MySqlConnection(_connection);
        await connection.OpenAsync();
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (reader.GetString(0).StartsWith(_prefix, StringComparison.Ordinal))
                    tables.Add(reader.GetString(0));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SET FOREIGN_KEY_CHECKS=0";
            await command.ExecuteNonQueryAsync();
        }

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE `" + table.Replace("`", "``") + "`";
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SET FOREIGN_KEY_CHECKS=1";
            await command.ExecuteNonQueryAsync();
        }
    }

    // A job of the stored shape, written by MySqlStorage as v2.7.1 wrote it, then enqueued
    private string Store(string capturedPayload)
    {
        var payload = capturedPayload.Replace(LegacyJobPayloads.CapturedRoot, _root.Replace('\\', '/'));
        var job = InvocationData.DeserializePayload(payload).DeserializeJob();
        using var connection = _storage.GetConnection();
        var id = connection.CreateExpiredJob(job, new Dictionary<string, string?>(), DateTime.UtcNow,
            TimeSpan.FromHours(1));
        // Enqueued as Hangfire enqueues: the state and the queue entry, in one transaction
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetJobState(id, new EnqueuedState());
        transaction.AddToQueue(EnqueuedState.DefaultQueue, id);
        transaction.PersistJob(id);
        transaction.Commit();
        return id;
    }

    private (string InvocationData, string Arguments) Row(string id)
    {
        using var connection = new MySqlConnection(_connection);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT InvocationData, Arguments FROM `{_prefix}Job` WHERE Id = @id";
        command.Parameters.AddWithValue("@id", int.Parse(id, System.Globalization.CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        return (reader.GetString(0), reader.GetString(1));
    }

    private async Task<string> RunUntilFinal(string id)
    {
        using var server = new BackgroundJobServer(new BackgroundJobServerOptions
        {
            WorkerCount = 1,
            ServerName = "legacyjobs-" + _prefix,
            Activator = new AspNetCoreJobActivator(_provider.GetRequiredService<IServiceScopeFactory>()),
            SchedulePollingInterval = TimeSpan.FromHours(1)
        }, _storage);

        string? state = null;
        // The worker commits the final state in its own storage transaction (no event to await)
        await Eventually.Until(() =>
        {
            state = _storage.GetMonitoringApi().JobDetails(id)?.History is { Count: > 0 } history
                ? history[0].StateName
                : null;
            return Task.FromResult(state == SucceededState.StateName || state == FailedState.StateName);
        }, $"job {id} to finish", TimeSpan.FromSeconds(60));
        return state!;
    }

    [Test]
    public async Task StoredBuildWrapper_RunsToSucceededOnDdbJobs()
    {
        var id = Store(LegacyJobPayloads.Captured["BuildWrapper"]);

        // The row is the stored shape: the legacy static method and the serialized DDB argument
        var (invocation, arguments) = Row(id);
        invocation.ShouldContain("Registry.Web.Utilities.HangfireUtils");
        invocation.ShouldContain("BuildWrapper");
        arguments.ShouldContain("Registry.Adapters.DroneDB.DDB, Registry.Adapters");

        (await RunUntilFinal(id)).ShouldBe(SucceededState.StateName);
        _wrapper.Verify(x => x.Build(_folder, "a/b.tif", null, false, false), Times.Once);
    }

    [Test]
    public async Task StoredCleanupWrapper_RunsToSucceededOnDdbJobs()
    {
        _wrapper.Setup(x => x.Cleanup(It.IsAny<string>())).Returns(new DdbCleanupResult());
        var id = Store(LegacyJobPayloads.Captured["CleanupWrapper"]);

        (await RunUntilFinal(id)).ShouldBe(SucceededState.StateName);
        _wrapper.Verify(x => x.Cleanup(_folder), Times.Once);
    }

    [Test]
    public async Task StoredSafeDelete_RunsToSucceededOnFileJobs()
    {
        var target = Path.Combine(_root, "artifact-dir");
        Directory.CreateDirectory(target);
        var id = Store(LegacyJobPayloads.Captured["SafeDelete"].Replace("/tmp/x", target.Replace('\\', '/')));

        (await RunUntilFinal(id)).ShouldBe(SucceededState.StateName);
        Directory.Exists(target).ShouldBeFalse();
    }

    [Test]
    public async Task StoredDummyJob_RunsToSucceeded()
    {
        var id = Store(LegacyJobPayloads.Captured["DummyJob"]);

        (await RunUntilFinal(id)).ShouldBe(SucceededState.StateName);
    }

    /// <summary>Releases what the last test left (TearDown already disposes each test's provider).</summary>
    public void Dispose() => _provider?.Dispose();
}
