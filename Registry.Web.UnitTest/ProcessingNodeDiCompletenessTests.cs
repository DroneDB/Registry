using Registry.Adapters.Testing;
using System;
using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
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
    private static ServiceProvider BuildNodeProvider(Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Registration-only; the Sqlite provider never opens this connection here.
                ["ConnectionStrings:RegistryConnection"] = "Data Source=:memory:",
                // The options are validated when a service reads them (RQ-07)
                ["AppSettings:DatasetsPath"] = "./datasets",
                ["AppSettings:CachePath"] = "./cache"
            })
            .Build();

        // Defaults: Sqlite registry provider and no cache provider (in-memory distributed
        // cache + NullCacheKeyScanner), so no external resources are required.
        var services = new ServiceCollection();
        services.AddLogging();
        // As the processing-node host (RegistryHost): libddb's runtime first (the fake: no native library here)
        services.AddDdbRuntime<FakeDdbRuntime>();
        services.AddProcessingNodeServices(configuration);
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
    public void ProcessingNode_ActivatesIndexRescanService()
    {
        // The system rescan job runs on the default queue, which processing nodes serve too.
        // A node gets the Hangfire client from AddHangfireProvider, which needs real storage.
        using var provider = BuildNodeProvider(s => s.AddSingleton(Mock.Of<IBackgroundJobClient>()));
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetService<Registry.Web.Services.Adapters.IndexRescanService>();

        service.ShouldNotBeNull();
    }
}
