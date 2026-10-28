#nullable disable
using Registry.Adapters.Testing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Regression tests asserting the processing-node container registers every service the
/// heavy tools resolve at runtime. Guards against the class of bug where a tool calls
/// <c>GetRequiredService</c> for a service that is only registered on the full web host
/// (e.g. the historical <c>IUtils</c>/<c>IObjectsManager</c> failures).
/// </summary>
/// <remarks>
/// The node container is built through the same <c>AddProcessingNodeServices</c> extension
/// used by <c>Program.RunAsProcessingNode</c>, so a missing registration fails here instead
/// of at runtime on a deployed processing node.
/// </remarks>
[TestFixture]
public class ProcessingNodeDiCompletenessTests
{
    private static ServiceProvider BuildNodeProvider(Action<IServiceCollection>? configure = null,
        InstanceType? role = null, string identityConnection = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Registration-only; the Sqlite provider never opens this connection here.
                ["ConnectionStrings:RegistryConnection"] = "Data Source=:memory:",
                ["ConnectionStrings:IdentityConnection"] = identityConnection,
                // The options are validated when a service reads them
                ["AppSettings:DatasetsPath"] = "./datasets",
                ["AppSettings:CachePath"] = "./cache",
                // As RegistryHost sets it for the role
                [Program.InstanceTypeKey] = role?.ToString()
            })
            .Build();

        // Defaults: Sqlite registry provider and no cache provider (in-memory distributed
        // cache + NullCacheKeyScanner), so no external resources are required.
        var services = new ServiceCollection();
        services.AddLogging();
        // As the processing-node host (RegistryHost): libddb's runtime first (the fake: no native library here)
        services.AddDdbRuntime<FakeDdbRuntime>();
        services.AddProcessingNodeServices(configuration);
        // As AddRegistryJobs (Hangfire) on every job host: the configured job storage
        services.AddSingleton<JobStorage>(new Hangfire.InMemory.InMemoryStorage());
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Test]
    public void ProcessingNode_RegistersDatasetCacheInvalidator()
    {
        using var provider = BuildNodeProvider();
        using var scope = provider.CreateScope();

        // Resolved at runtime by ArchiveExtractTool and RescanIndexTool.
        var invalidator = scope.ServiceProvider.GetService<IDatasetCacheInvalidator>();

        invalidator.ShouldNotBeNull();
    }

    [Test]
    public void ProcessingNode_AllHeavyToolsActivate()
    {
        using var provider = BuildNodeProvider();
        using var scope = provider.CreateScope();

        // Activating every registered tool proves their constructor dependencies are all
        // present on the node. Tools that resolve further services at runtime (via a child
        // scope) are covered by the explicit assertions above.
        var tools = scope.ServiceProvider.GetServices<IHeavyTool>().ToList();

        tools.ShouldNotBeEmpty();
        tools.ShouldAllBe(t => t != null);
    }

    [Test]
    public void ProcessingNode_HeavyToolsActivateWithoutTheJobStorage()
    {
        // The Hangfire filters are installed from inside Hangfire's configuration (InstallJobFilters),
        // and the queue routing filter takes the tool registry: a tool whose construction needs the job
        // storage re-enters that configuration ("Console is already initialized" at the host start)
        using var provider = BuildNodeProvider(s => s.AddSingleton<JobStorage>(_ =>
            throw new InvalidOperationException("A heavy tool needs the job storage to be constructed")));
        using var scope = provider.CreateScope();

        Should.NotThrow(() => scope.ServiceProvider.GetRequiredService<IHeavyToolRegistry>());
        scope.ServiceProvider.GetServices<IHeavyTool>().ShouldNotBeEmpty();
    }

    [Test]
    public void ProcessingNode_RegistersDatasetIndexQueue()
    {
        using var provider = BuildNodeProvider();
        using var scope = provider.CreateScope();

        // The IndexReconciliationService recurring job (registered on this host too, since
        // HangfireJobsInitializer runs on every job-executing host) re-enqueues unindexed
        // files through this singleton.
        var queue = scope.ServiceProvider.GetService<IDatasetIndexQueue>();

        queue.ShouldNotBeNull();
    }

    [Test]
    public void ProcessingNode_ActivatesIndexReconciliationService()
    {
        using var provider = BuildNodeProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetService<Registry.Web.Services.Adapters.IndexReconciliationService>();

        service.ShouldNotBeNull();
    }

    [Test]
    public void ProcessingNode_ActivatesTheUpdateCheck_AndHasTheInstanceIdentityTask()
    {
        // The update e-mail sends through IEmailService, whose queue is the Hangfire client of the job storage
        using var provider = BuildNodeProvider(s => s.AddSingleton(Mock.Of<IBackgroundJobClient>()));
        using var scope = provider.CreateScope();

        // The recurring job is activated by its interface; the identity is created by a startup task of every job host
        scope.ServiceProvider.GetRequiredService<IUpdateCheckService>().ShouldBeOfType<Registry.Web.Services.UpdateCheck.UpdateCheckService>();
        scope.ServiceProvider.GetServices<Registry.Web.Hosting.IStartupTask>()
            .ShouldContain(task => task.GetType().Name == "InstanceIdInitializer");
        scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(Registry.Web.Services.UpdateCheck.UpdateCheckClient.HttpClientName)
            .Timeout.ShouldBe(Registry.Web.Services.UpdateCheck.UpdateCheckClient.Timeout);
    }

    [TestCase(typeof(Registry.Web.Services.Jobs.DdbJobs))]
    [TestCase(typeof(Registry.Web.Services.Jobs.FileJobs))]
    [TestCase(typeof(Registry.Web.Services.Jobs.ExpiredRecurringJobsSweep))]
    [TestCase(typeof(Registry.Web.Services.Jobs.HealthCheckJob))]
    [TestCase(typeof(HangfireUtils))]
    [TestCase(typeof(Registry.Web.Services.HeavyTasks.Adapters.HeavyTaskJobWrapper))]
    [TestCase(typeof(Registry.Web.Services.RemoteImport.RemoteImportJob))] // Queued by the web host
    [TestCase(typeof(Registry.Web.Services.BuildNotifications.BuildNotificationDigestJob))] // With the dataset access check
    public void ProcessingNode_ActivatesTheJobClasses(Type jobClass)
    {
        // Hangfire's activator creates a job's class from the job scope: the stored shapes too
        using var provider = BuildNodeProvider(s => s.AddSingleton(Mock.Of<IBackgroundJobClient>()));
        using var scope = provider.CreateScope();

        Microsoft.Extensions.DependencyInjection.ActivatorUtilities
            .GetServiceOrCreateInstance(scope.ServiceProvider, jobClass).ShouldNotBeNull();
    }

    [Test]
    public void ProcessingNode_ActivatesIndexRescanService()
    {
        // The system rescan job runs on the default queue, which processing nodes serve too.
        // A node gets the Hangfire client from AddHangfireProvider, which needs real storage.
        using var provider = BuildNodeProvider(s => s.AddSingleton(Mock.Of<IBackgroundJobClient>()));
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetService<Registry.Web.Services.Adapters.IndexRescanService>();

        service.ShouldNotBeNull();
    }

    [Test]
    public void ProcessingNode_TellsTheAdministrators_FromTheIdentityDatabaseItShares()
    {
        using var provider = BuildNodeProvider(s => s.AddSingleton(Mock.Of<IBackgroundJobClient>()), InstanceType.ProcessingNode);
        using var scope = provider.CreateScope();

        // The update check job runs on whichever node serves the default queue: the node has no authentication, so the
        // notifier needs the identity database registered for it
        scope.ServiceProvider.GetRequiredService<IUpdateNotifier>().ShouldBeOfType<Registry.Web.Services.UpdateCheck.UpdateNotifier>();
        scope.ServiceProvider.GetRequiredService<IAdminRecipients>().ShouldBeOfType<Registry.Web.Services.Adapters.IdentityAdminRecipients>();
        scope.ServiceProvider.GetService<Registry.Web.Identity.ApplicationDbContext>().ShouldNotBeNull();
    }

    [Test]
    public async Task ProcessingNode_ReadsTheAdministratorsOfTheDatabaseTheWebHostMigrated()
    {
        var folder = Path.Combine(Path.GetTempPath(), "registry-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connection = $"Data Source={Path.Combine(folder, "identity.db")};Mode=ReadWriteCreate;Pooling=False";

        try
        {
            // What the web host left in the shared database
            await using (var web = new Registry.Web.Identity.ApplicationDbContext(
                             new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Registry.Web.Identity.ApplicationDbContext>()
                                 .UseSqlite(connection).Options))
            {
                await web.Database.EnsureCreatedAsync();
                var admin = new Microsoft.AspNetCore.Identity.IdentityRole("admin") { NormalizedName = "ADMIN" };
                web.Roles.Add(admin);
                web.Users.Add(new Registry.Web.Identity.Models.User { Id = "u1", UserName = "root", Email = "root@example.com" });
                web.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = "u1", RoleId = admin.Id });
                await web.SaveChangesAsync();
            }

            using var provider = BuildNodeProvider(role: InstanceType.ProcessingNode, identityConnection: connection);
            using var scope = provider.CreateScope();

            var recipients = await scope.ServiceProvider.GetRequiredService<IAdminRecipients>().GetAsync(default);

            recipients.Select(r => r.Address).ShouldBe(["root@example.com"]);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [TestCase(InstanceType.Default)]
    [TestCase(InstanceType.WebServer)]
    [TestCase(InstanceType.ProcessingNode)]
    public void EveryJobHost_RegistersTheIdentityDatabaseOnce(InstanceType role)
    {
        // The web host's authentication (AddRegistryAuth) reuses this registration: a second one would configure the context twice
        var registrations = 0;
        using var provider = BuildNodeProvider(s =>
        {
            registrations = s.Count(d => d.ServiceType == typeof(DbContextOptions<Registry.Web.Identity.ApplicationDbContext>));
            s.AddSingleton(Mock.Of<IBackgroundJobClient>());
        }, role);
        using var scope = provider.CreateScope();

        registrations.ShouldBe(1);
        scope.ServiceProvider.GetService<Registry.Web.Identity.ApplicationDbContext>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<IAdminRecipients>().ShouldBeOfType<Registry.Web.Services.Adapters.IdentityAdminRecipients>();
    }
}
