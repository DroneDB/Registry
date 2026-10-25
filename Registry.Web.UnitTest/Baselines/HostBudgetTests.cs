#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// One budget per host, split by role over the Registry processes
/// that share the machine, and passed to libddb through <c>DDBConfigure</c>. Without a budget each process sizes
/// itself to the whole machine (prod: the web server and 2 nodes on one machine, each node planning every
/// core). With no budget declared the sizing is unchanged and no derived libddb value is sent.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~HostBudgetTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class HostBudgetTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly HostFacts Machine = new(16, 64 * GiB);

    private static HostResourceBudget Budget(InstanceType role, HostBudgetSettings? host = null,
        DdbRuntimeOptions? ddb = null, IReadOnlyDictionary<string, string>? environment = null,
        ProcessingBudgetSettings? jobs = null) =>
        HostResourceBudget.Create(role, Machine,
            new HostResourceOptions { HostBudget = host ?? new HostBudgetSettings(), ProcessingBudget = jobs ?? new ProcessingBudgetSettings() },
            ddb ?? new DdbRuntimeOptions(), name => environment?.GetValueOrDefault(name));

    /// <summary>The production compose file's topology: a web server and two processing nodes.</summary>
    private static readonly HostBudgetSettings Production = new() { WebServers = 1, ProcessingNodes = 2 };

    [TestCase(InstanceType.Default)]
    [TestCase(InstanceType.WebServer)]
    [TestCase(InstanceType.ProcessingNode)]
    [TestCase(InstanceType.ThumbnailGenerator)]
    public void NothingDeclared_TheWholeMachine_AndNoDerivedLibddbValue(InstanceType role)
    {
        var budget = Budget(role);

        budget.Declared.ShouldBeFalse();
        budget.Share.ShouldBe(1);
        budget.Process.ShouldBe(Machine);
        budget.Ddb.ShouldBe(new DdbProcessSettings());
        if (role is InstanceType.Default or InstanceType.ProcessingNode)
            budget.Processing.ShouldBe(ProcessingBudget.Derive(Machine, new ProcessingBudgetSettings(), 0));
        else
            budget.Processing.ShouldBeNull();
        budget.RenderTiles.ShouldBe(new NativeConcurrencySettings().TilesFor(Machine.Cores));
    }

    [Test]
    public void ProductionTopology_SplitsTheMachine_ByWeight()
    {
        var web = Budget(InstanceType.WebServer, Production);
        var node = Budget(InstanceType.ProcessingNode, Production);

        web.Share.ShouldBe(0.2, 1e-9);
        node.Share.ShouldBe(0.4, 1e-9);
        web.Process.Cores.ShouldBe(3);
        node.Process.Cores.ShouldBe(6);
        (web.Process.Cores + 2 * node.Process.Cores).ShouldBeLessThanOrEqualTo(Machine.Cores);
        (web.Process.MemoryBytes + 2 * node.Process.MemoryBytes).ShouldBeLessThanOrEqualTo(Machine.MemoryBytes);

        node.Processing.ShouldBe(ProcessingBudget.Derive(node.Process, new ProcessingBudgetSettings(), 0));
        web.Processing.ShouldBeNull();

        // libddb's CPU pool beside the calling thread, per process
        web.Ddb.CpuPoolSize.ShouldBe(2);
        node.Ddb.CpuPoolSize.ShouldBe(5);

        // The web server's render lanes on its share
        web.RenderTiles.ShouldBe(2);
        web.RenderHeavy.ShouldBe(1);
    }

    /// <summary>Without a budget each node planned all 16 cores: 2 nodes x 8 build threads on a 16-core machine with the web server.</summary>
    [Test]
    public void ProductionTopology_NodesNoLongerPlanEveryCore()
    {
        var whole = Budget(InstanceType.ProcessingNode).Processing!;
        var shared = Budget(InstanceType.ProcessingNode, Production).Processing!;

        (2 * whole.HeavyWorkers * whole.BuildThreads).ShouldBeGreaterThan(Machine.Cores, "the defect: every node sized to the machine");
        (2 * shared.HeavyWorkers * shared.BuildThreads).ShouldBeLessThanOrEqualTo(Machine.Cores);
    }

    [Test]
    public void AllInOneInstance_HasTheWebAndNodeWeights()
    {
        var budget = Budget(InstanceType.Default, new HostBudgetSettings { ProcessingNodes = 1 });

        budget.Share.ShouldBe(3.0 / 5, 1e-9);
    }

    [Test]
    public void ExplicitLibddbSettings_Win()
    {
        var budget = Budget(InstanceType.ProcessingNode, Production,
            new DdbRuntimeOptions
            {
                CpuPoolSize = 12, IoThreads = 7, MinFreeDiskMb = 2048, CacheDir = "/data/temp/ddb-cache", GdalCacheMb = 384
            });

        budget.Ddb.ShouldBe(new DdbProcessSettings(12, 7, 2048, "/data/temp/ddb-cache", 384));
    }

    [Test]
    public void EnvironmentVariables_WinOverDerivedValues()
    {
        var host = new HostBudgetSettings { WebServers = 1, ProcessingNodes = 2, IoThreads = 8 };
        var environment = new Dictionary<string, string>
        {
            [HostResourceBudget.CpuPoolSizeVariable] = "4",
            [HostResourceBudget.IoThreadsVariable] = "2"
        };

        Budget(InstanceType.ProcessingNode, host, environment: environment).Ddb.ShouldBe(new DdbProcessSettings());
    }

    [Test]
    public void IoThreads_AreSplitLikeTheCores_AtLeastOne()
    {
        var host = new HostBudgetSettings { WebServers = 1, ProcessingNodes = 2, ThumbnailGenerators = 1, IoThreads = 4 };

        Budget(InstanceType.ProcessingNode, host).Ddb.IoThreads.ShouldBe(1); // 4 x 2/6
        Budget(InstanceType.ThumbnailGenerator, host).Ddb.IoThreads.ShouldBe(1); // 4 x 1/6, at least 1
        Budget(InstanceType.ProcessingNode, new HostBudgetSettings { WebServers = 1, ProcessingNodes = 2, ThumbnailGenerators = 1, IoThreads = 12 }).Ddb.IoThreads.ShouldBe(4);
    }

    [Test]
    public void DeclaredMachineSize_ReplacesTheDetectedOne()
    {
        var budget = Budget(InstanceType.ProcessingNode, new HostBudgetSettings { Cores = 8, MemoryMb = 16384, ProcessingNodes = 1 });

        budget.Machine.ShouldBe(new HostFacts(8, 16 * GiB));
        budget.For(InstanceType.WebServer).Machine.ShouldBe(budget.Machine);
    }

    [Test]
    public void ExplicitJobBudget_StillWins()
    {
        var budget = Budget(InstanceType.ProcessingNode, Production, jobs: new ProcessingBudgetSettings { HeavyWorkers = 3, BuildThreads = 2 });

        budget.Processing!.HeavyWorkers.ShouldBe(3);
        budget.Processing.BuildThreads.ShouldBe(2);
    }

    /// <summary>The production compose file declares its own processes, on every Registry service.</summary>
    [Test]
    public void ProductionCompose_DeclaresTheProcessesItRuns()
    {
        Assume.That(RepositoryFiles.Root, Is.Not.Null, "repository sources not available next to the test output");
        var compose = System.IO.File.ReadAllText(RepositoryFiles.PathOf("docker/production/docker-compose.yml"));

        var nodes = System.Text.RegularExpressions.Regex.Count(compose, @"- INSTANCE_TYPE=1\b");
        var webServers = System.Text.RegularExpressions.Regex.Count(compose, @"- INSTANCE_TYPE=2\b");
        var services = nodes + webServers;

        System.Text.RegularExpressions.Regex.Count(compose, $@"- AppSettings__HostBudget__ProcessingNodes={nodes}\b").ShouldBe(services);
        System.Text.RegularExpressions.Regex.Count(compose, $@"- AppSettings__HostBudget__WebServers={webServers}\b").ShouldBe(services);
    }

    /// <summary>The runtime service passes the budget to libddb, and sets the per-build environment of a job host only.</summary>
    [TestCase(InstanceType.ProcessingNode, true)]
    [TestCase(InstanceType.WebServer, false)]
    public async Task RuntimeService_ConfiguresLibddbWithTheBudget(InstanceType role, bool buildEnvironment)
    {
        var runtime = new FakeDdbRuntime();
        // The deployment set one of them: it wins over the budget
        runtime.Environment["DDB_WARP_MEMORY_MB"] = "2048";
        var budget = Budget(role, Production);
        var service = new DdbRuntimeService(runtime, budget, new DdbLogSink(NullLoggerFactory.Instance),
            NullLogger<DdbRuntimeService>.Instance);

        await service.StartingAsync(CancellationToken.None);

        runtime.Settings.ShouldBe(budget.Ddb);
        runtime.Calls.ShouldBe(["SetLogSink", "Configure", "RegisterProcess"]);
        runtime.Environment["DDB_WARP_MEMORY_MB"].ShouldBe("2048");
        if (buildEnvironment)
        {
            runtime.Environment["DDB_BUILD_THREADS"].ShouldBe(budget.Processing!.BuildThreads.ToString(System.Globalization.CultureInfo.InvariantCulture));
            runtime.Environment["DDB_COG_CACHE_MB"].ShouldBe(budget.Processing.BuildCacheMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
            runtime.Environment.Keys.ShouldBe(["DDB_WARP_MEMORY_MB"], "a host that runs no builds sets no build limit");
    }
}
