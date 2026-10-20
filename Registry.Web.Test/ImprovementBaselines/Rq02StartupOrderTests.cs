#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-02 item 1 (startup order): the database migrations, the cache provider registration and the
/// recurring job registration done by <c>AppInitializer</c> complete before any hosted service starts,
/// so before the real Hangfire <see cref="BackgroundJobServerHostedService"/>s (and the upload queues)
/// registered ahead of it in <c>Startup</c>. Hosted services start in registration order; the fix runs
/// the initializer in <c>IHostedLifecycleService.StartingAsync</c>, which the host runs for every
/// lifecycle service before any <c>StartAsync</c>.
/// <para>
/// Observable: a probe registered as the very first hosted service (so its <c>StartAsync</c> runs before
/// every other <c>StartAsync</c>, Hangfire's included) records the initialization state the moment the
/// host starts the hosted services. The host runs as a Default instance, so <c>Startup</c> registers
/// the real budgeted Hangfire servers (in-memory storage, mocked libddb).
/// </para>
/// Run: dotnet test Registry.Web.Test --filter "FullyQualifiedName~Rq02StartupOrderTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("ImprovementBaseline")]
public class Rq02StartupOrderTests
{
    // Startup.AddBudgetedHangfireServers hands the processing budget to libddb through the native
    // environment when these are unset: restore them so the boot leaves no process-wide trace.
    private static readonly string[] NativeBudgetVariables =
        ["DDB_BUILD_THREADS", "DDB_COG_CACHE_MB", "DDB_WARP_MEMORY_MB"];

    private Dictionary<string, string?> _savedEnvironment = null!;

    [SetUp]
    public void SetUp() =>
        _savedEnvironment = NativeBudgetVariables.ToDictionary(n => n, Environment.GetEnvironmentVariable);

    [TearDown]
    public void TearDown()
    {
        foreach (var (name, value) in _savedEnvironment)
            SetNativeEnvironment(name, value);
    }

    [Test]
    public void Initialization_CompletesBeforeAnyHostedServiceStarts_IncludingHangfireServers()
    {
        var probe = new HostStartProbe();

        using var factory = new RegistryWebFactory(
            settings: new Dictionary<string, string>
            {
                // Real Hangfire servers (Startup registers them only for the Default instance type)
                ["InstanceType"] = nameof(InstanceType.Default),
                // The only per-minute recurring job: keep it from firing while the servers run
                ["AppSettings:RecoverIndexedJobPublicationsCron"] = "disabled"
            },
            // Index 0 = first hosted service: its StartAsync precedes every other StartAsync
            configureServices: services => services.Insert(0,
                ServiceDescriptor.Singleton<IHostedService>(sp => probe.Bind(sp))));

        _ = factory.Services; // boots the host

        // The servers under test are Hangfire's own hosted services, registered by Startup
        var hostedServices = factory.Services.GetServices<IHostedService>().ToArray();
        hostedServices[0].ShouldBeSameAs(probe);
        hostedServices.OfType<BackgroundJobServerHostedService>().Count().ShouldBe(2);

        var snapshot = probe.Snapshot.ShouldNotBeNull();
        snapshot.PendingRegistryMigrations.ShouldBeEmpty(
            "the registry database must be migrated before the first hosted service starts");
        snapshot.PendingIdentityMigrations.ShouldBeEmpty(
            "the identity database must be migrated before the first hosted service starts");
        snapshot.ThumbnailCacheRegistered.ShouldBeTrue(
            "the cache providers must be registered before the first hosted service starts");
        snapshot.RecurringJobIds.ShouldContain("cleanup-expired-jobs",
            "the recurring jobs must be registered before the Hangfire servers start");
    }

    private sealed record StartupSnapshot(
        string[] PendingRegistryMigrations,
        string[] PendingIdentityMigrations,
        bool ThumbnailCacheRegistered,
        string[] RecurringJobIds);

    /// <summary>
    /// Test-only hosted service that captures the initialization state in its <c>StartAsync</c>.
    /// It never throws there (that would abort the host start): the test asserts on the snapshot.
    /// </summary>
    private sealed class HostStartProbe : IHostedService
    {
        private IServiceProvider? _services;

        public StartupSnapshot? Snapshot { get; private set; }

        // Called by the singleton registration factory: captures the root provider
        public HostStartProbe Bind(IServiceProvider services)
        {
            _services = services;
            return this;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var services = _services ?? throw new InvalidOperationException("Probe not bound");
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;

            var registry = sp.GetRequiredService<RegistryContext>();
            var identity = sp.GetRequiredService<ApplicationDbContext>();

            using var connection = sp.GetRequiredService<JobStorage>().GetConnection();

            Snapshot = new StartupSnapshot(
                (await registry.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray(),
                (await identity.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray(),
                sp.GetRequiredService<ICacheManager>().IsRegistered(MagicStrings.ThumbnailCacheSeed),
                connection.GetRecurringJobs().Select(j => j.Id).ToArray());
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Native getenv must see the restored value too (libddb reads the native environment)
    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnv(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "unsetenv")]
    private static extern int UnsetEnv(string name);

    [DllImport("ucrtbase", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PutEnv(string name, string value);

    private static void SetNativeEnvironment(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        var result = OperatingSystem.IsWindows() ? PutEnv(name, value ?? "") :
            value == null ? UnsetEnv(name) : SetEnv(name, value, 1);
        result.ShouldBe(0);
    }
}
