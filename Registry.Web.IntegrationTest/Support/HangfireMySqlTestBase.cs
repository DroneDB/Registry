using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.MySql;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Registry.Test.Common;

using Registry.Web.Utilities;

namespace Registry.Web.Test.Support;

/// <summary>
/// The base of the tests that run Hangfire jobs on the production job storage (the MySQL storage on the shared MariaDB
/// test server, <see cref="MariaDbServer"/>): each test owns the tables of a random prefix, dropped at its end.
/// </summary>
[NonParallelizable]
[Category(TestCategories.Network)]
public abstract class HangfireMySqlTestBase
{
    protected string Connection { get; private set; } = null!;

    protected string TablesPrefix { get; private set; } = null!;

    private MySqlStorage? _storage;

    [SetUp]
    public async Task SetUpStorage()
    {
        Connection = (await MariaDbServer.GetAsync()).ConnectionString;
        TablesPrefix = "runctl" + Guid.NewGuid().ToString("N")[..12];
        GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
    }

    [TearDown]
    public async Task TearDownStorage()
    {
        _storage?.Dispose();
        // SetUp ignored the test before it made anything (no Docker outside CI)
        if (TablesPrefix is not null && Connection is not null)
            await MariaDbServer.DropTablesOfPrefixAsync(Connection, TablesPrefix);
    }

    /// <summary>The storage of the test (disposed at its end); <paramref name="invisibilityTimeout"/> replaces the 5-minute one.</summary>
    protected MySqlStorage Storage(TimeSpan? invisibilityTimeout = null)
    {
        var options = MariaDbServer.HangfireStorageOptions(TablesPrefix);
        if (invisibilityTimeout is { } timeout)
#pragma warning disable CS0618
            options.InvisibilityTimeout = timeout;
#pragma warning restore CS0618
        return _storage = new MySqlStorage(Connection, options);
    }

    /// <summary>The configuration of a host with the Hangfire connection string of the test server.</summary>
    protected IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{MagicStrings.HangfireConnectionName}"] = Connection
        }).Build();
}
