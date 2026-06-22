using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    private static ServiceProvider BuildNodeProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Registration-only; the Sqlite provider never opens this connection here.
                ["ConnectionStrings:RegistryConnection"] = "Data Source=:memory:"
            })
            .Build();

        // Defaults: Sqlite registry provider and no cache provider (in-memory distributed
        // cache + NullCacheKeyScanner), so no external resources are required.
        var appSettings = new AppSettings();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProcessingNodeServices(configuration, appSettings);
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
}
