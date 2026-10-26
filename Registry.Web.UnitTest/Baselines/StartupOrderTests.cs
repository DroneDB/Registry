#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Initialization;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The database migrations, the cache provider registration and the
/// recurring job registration (the startup tasks of <c>StartupTaskRunner</c>) complete before any hosted service starts,
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
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~StartupOrderTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class StartupOrderTests
{
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
        hostedServices.OfType<BackgroundJobServerHostedService>().Count().ShouldBe(3, "builds, rebuilds and the other queues");

        var snapshot = probe.Snapshot.ShouldNotBeNull();
        snapshot.PendingRegistryMigrations.ShouldBeEmpty(
            "the registry database must be migrated before the first hosted service starts");
        snapshot.PendingIdentityMigrations.ShouldBeEmpty(
            "the identity database must be migrated before the first hosted service starts");
        snapshot.ThumbnailCacheRegistered.ShouldBeTrue(
            "the cache providers must be registered before the first hosted service starts");
        snapshot.RecurringJobIds.ShouldContain("cleanup-expired-jobs",
            "the recurring jobs must be registered before the Hangfire servers start");
        snapshot.InstanceIdentityFileExists.ShouldBeTrue(
            "the instance identity must exist before the first hosted service starts");
        snapshot.RecurringJobIds.ShouldContain(RecurringJobIds.CheckUpdates,
            "the update check job is defined from the identity, so it is defined with the other recurring jobs");
    }

    private sealed record StartupSnapshot(
        string[] PendingRegistryMigrations,
        string[] PendingIdentityMigrations,
        bool ThumbnailCacheRegistered,
        string[] RecurringJobIds,
        bool InstanceIdentityFileExists);

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
                connection.GetRecurringJobs().Select(j => j.Id).ToArray(),
                File.Exists(Path.Combine(sp.GetRequiredService<IOptions<StorageOptions>>().Value.StoragePath!, MagicStrings.InstanceIdFileName)));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
