#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Microting.EntityFrameworkCore.MySql.Infrastructure;
using Registry.Web.Data;
using Registry.Web.Models.Configuration;
using Registry.Web.Utilities;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>ServerVersion.AutoDetect</c> ran inside the per-scope
/// <c>AddDbContext</c> options lambda, so every request scope and every job scope opened one extra
/// MySQL connection per context just to read the server version. After the fix the version is
/// resolved once per connection string, lazily on first use (a database that is not ready yet must
/// not crash start-up), and a pinned <c>AppSettings:MySqlServerVersion</c> skips the probe entirely.
/// <para>
/// Measurement: before, 1 AutoDetect connection per context per scope; after, 1 per connection
/// string per process (0 when pinned). The live connection count on MariaDB is
/// <c>MySqlLiveTests.VersionProbe_ManyScopesOfBothContexts_OpenOneConnectionPerConnectionString</c> (Registry.Web.IntegrationTest).
/// </para>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~MySqlVersionProbeTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class MySqlVersionProbeTests
{
    private const string ConnectionString = "Server=127.0.0.1;Port=1;Database=RegistryData;Uid=u;Pwd=p;";
    private static readonly ServerVersion Detected = ServerVersion.Parse("8.0.36-mysql");

    private sealed class CountingProbe
    {
        private int _calls;
        public int Calls => _calls;
        public Func<string, ServerVersion>? Behaviour { get; set; }

        public ServerVersion Probe(string connectionString)
        {
            Interlocked.Increment(ref _calls);
            return Behaviour?.Invoke(connectionString) ?? Detected;
        }
    }

    [Test]
    public void Resolve_SameConnectionString_ProbesOnce()
    {
        var probe = new CountingProbe();
        var cache = new MySqlServerVersionCache(probe: probe.Probe);

        for (var i = 0; i < 10; i++)
            cache.Resolve(ConnectionString).ShouldBe(Detected);

        probe.Calls.ShouldBe(1);
    }

    [Test]
    public void Resolve_DistinctConnectionStrings_ProbesOncePerConnectionString()
    {
        var probe = new CountingProbe();
        var cache = new MySqlServerVersionCache(probe: probe.Probe);

        for (var i = 0; i < 5; i++)
        {
            cache.Resolve(ConnectionString);
            cache.Resolve(ConnectionString + "Pooling=true;");
        }

        probe.Calls.ShouldBe(2);
    }

    [Test]
    public void Resolve_ConcurrentFirstUse_ProbesOnce()
    {
        var probe = new CountingProbe();
        var cache = new MySqlServerVersionCache(probe: probe.Probe);

        Parallel.For(0, 32, _ => cache.Resolve(ConnectionString));

        probe.Calls.ShouldBe(1);
    }

    [Test]
    public void Resolve_PinnedVersion_NeverProbes()
    {
        var probe = new CountingProbe();
        var cache = new MySqlServerVersionCache("5.7.44-mysql", probe.Probe);

        var version = cache.Resolve(ConnectionString);

        probe.Calls.ShouldBe(0);
        version.Version.ShouldBe(new Version(5, 7, 44));
        version.Type.ShouldBe(ServerType.MySql);
    }

    [Test]
    public void Resolve_FailedProbe_IsNotCached()
    {
        // A database that is not ready yet must not poison the process for its whole lifetime
        var probe = new CountingProbe { Behaviour = _ => throw new InvalidOperationException("not ready") };
        var cache = new MySqlServerVersionCache(probe: probe.Probe);

        Should.Throw<InvalidOperationException>(() => cache.Resolve(ConnectionString));

        probe.Behaviour = null;
        cache.Resolve(ConnectionString).ShouldBe(Detected);
        cache.Resolve(ConnectionString).ShouldBe(Detected);

        probe.Calls.ShouldBe(2);
    }

    [Test]
    public void AddDbContextWithProvider_OptionsBuiltInManyScopes_ProbesOnce()
    {
        var probe = new CountingProbe();
        using var provider = BuildProvider(new Dictionary<string, string?>(),
            new MySqlServerVersionCache(probe: probe.Probe));

        for (var i = 0; i < 5; i++)
        {
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<DbContextOptions<RegistryContext>>();
        }

        probe.Calls.ShouldBe(1);
    }

    [Test]
    public void AddDbContextWithProvider_PinnedInConfiguration_OpensNoConnection()
    {
        // No cache passed: the production path reads the pin from configuration. The connection
        // string points at a closed port, so any AutoDetect attempt would throw.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            [MySqlServerVersionCache.PinnedVersionKey] = "8.0.36-mysql"
        }, versions: null);

        for (var i = 0; i < 3; i++)
        {
            using var scope = provider.CreateScope();
            Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<DbContextOptions<RegistryContext>>());
        }
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings,
        MySqlServerVersionCache? versions)
    {
        settings["ConnectionStrings:RegistryMysqlConnection"] = ConnectionString;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddDbContextWithProvider<RegistryContext>(configuration, DbProvider.Mysql,
            "RegistryMysqlConnection", "Data", versions);
        return services.BuildServiceProvider();
    }
}
