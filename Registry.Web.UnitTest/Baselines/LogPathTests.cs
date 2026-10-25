#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Registry.Web.Hosting;
using Registry.Web.Utilities;
using Serilog;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The web server, the processing nodes and the
/// thumbnail generator share one storage folder and wrote the same <c>./logs/registry.txt</c>. The
/// File sink's <c>shared: true</c> serializes writers with a named mutex, which does not cross
/// containers. The one <c>RegistryHost.ConfigureSerilog</c> now derives the file name from the instance
/// type (<c>registry-web</c>, <c>registry-node</c>, <c>registry-thumbs</c>; the default all-in-one
/// instance keeps the configured name), for whatever path the configuration holds. Several processing
/// nodes run in one deployment (docker-compose <c>registry_node_1</c>, <c>registry_node_2</c>) on the
/// same folder, so the node file also carries the sanitised machine name (<c>registry-node-&lt;host&gt;</c>).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~LogPathTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class LogPathTests
{
    private const string DefaultSettingsResourceSuffix = "appsettings-default.json";

    private string _folder = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), "logpath-log-path", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private static Stream ReadDefaultSettings()
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(DefaultSettingsResourceSuffix, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(name)!;
    }

    /// <summary>
    /// Builds the logger of a host of <paramref name="instanceType"/> through
    /// <c>RegistryHost.ConfigureSerilog</c> (shipped Serilog section, file sink moved to the temp folder),
    /// writes one event and returns the file names written.
    /// </summary>
    private string[] WriteThroughConfigureSerilog(InstanceType? instanceType, string fileName = "registry.txt")
    {
        var folder = Path.Combine(_folder, instanceType?.ToString() ?? "unset");
        Directory.CreateDirectory(folder);

        var settings = new Dictionary<string, string?>
        {
            // WriteTo:0 is Console, WriteTo:1 the File sink (same index RegistryWebFactory overrides)
            ["Serilog:WriteTo:1:Args:path"] = Path.Combine(folder, fileName)
        };
        if (instanceType != null)
            settings["InstanceType"] = instanceType.ToString();

        using var defaults = ReadDefaultSettings();
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(defaults)
            .AddInMemoryCollection(settings)
            .Build();

        var context = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration };
        using var services = new ServiceCollection().BuildServiceProvider();

        var loggerConfiguration = new LoggerConfiguration();
        RegistryHost.ConfigureSerilog(context, services, loggerConfiguration);
        using (var logger = loggerConfiguration.CreateLogger())
            logger.Warning("logpath log path");

        return Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToArray();
    }

    [TestCase(InstanceType.WebServer, @"^registry-web\d{8}\.txt$")]
    [TestCase(InstanceType.ProcessingNode, @"^registry-node-[a-z0-9-]+\d{8}\.txt$")]
    [TestCase(InstanceType.ThumbnailGenerator, @"^registry-thumbs\d{8}\.txt$")]
    [TestCase(InstanceType.Default, @"^registry\d{8}\.txt$")]
    public void ConfigureSerilog_FileName_FollowsTheInstanceType(InstanceType instanceType, string pattern)
    {
        WriteThroughConfigureSerilog(instanceType).ShouldHaveSingleItem().ShouldMatch(pattern);
    }

    /// <summary>The three instance types of a docker deployment never open the same file.</summary>
    [Test]
    public void ConfigureSerilog_WebServerAndProcessingNode_WriteDifferentFiles()
    {
        var web = WriteThroughConfigureSerilog(InstanceType.WebServer).ShouldHaveSingleItem();
        var node = WriteThroughConfigureSerilog(InstanceType.ProcessingNode).ShouldHaveSingleItem();
        var thumbs = WriteThroughConfigureSerilog(InstanceType.ThumbnailGenerator).ShouldHaveSingleItem();

        new[] { web, node, thumbs }.Distinct().Count().ShouldBe(3);
    }

    /// <summary>A host that does not set the instance type keeps the configured name.</summary>
    [Test]
    public void ConfigureSerilog_NoInstanceType_KeepsTheConfiguredName()
    {
        WriteThroughConfigureSerilog(null).ShouldHaveSingleItem().ShouldMatch(@"^registry\d{8}\.txt$");
    }

    /// <summary>A configured path keeps working: only the file name gets the instance suffix.</summary>
    [Test]
    public void ConfigureSerilog_CustomConfiguredName_GetsTheSuffix()
    {
        WriteThroughConfigureSerilog(InstanceType.ProcessingNode, "custom.log")
            .ShouldHaveSingleItem().ShouldMatch(@"^custom-node-[a-z0-9-]+\d{8}\.log$");
    }

    // Several processing nodes on one folder (docker-compose registry_node_1, registry_node_2)

    private const string ConfiguredLogPath = "./logs/registry.txt";

    private static IConfiguration NodeConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstanceType"] = nameof(InstanceType.ProcessingNode),
            ["Serilog:WriteTo:0:Name"] = "File",
            ["Serilog:WriteTo:0:Args:path"] = ConfiguredLogPath
        })
        .Build();

    [Test]
    public void ProcessingNodes_OnDifferentMachines_WriteDifferentFiles()
    {
        var node1 = InstanceLogPaths.Apply(NodeConfiguration(), "registry-node-1")["Serilog:WriteTo:0:Args:path"];
        var node2 = InstanceLogPaths.Apply(NodeConfiguration(), "registry-node-2")["Serilog:WriteTo:0:Args:path"];

        node1.ShouldBe(Path.Combine("./logs", "registry-node-registry-node-1.txt"));
        node2.ShouldBe(Path.Combine("./logs", "registry-node-registry-node-2.txt"));
    }

    [TestCase("Node_A.example.org", "registry-node-node-a-example-org.txt")]
    [TestCase("  WORKER7  ", "registry-node-worker7.txt")]
    [TestCase("", "registry-node.txt")]
    [TestCase(null, "registry-node.txt")]
    public void ProcessingNode_MachineName_IsSanitised(string? machineName, string expected)
    {
        InstanceLogPaths.PathFor(ConfiguredLogPath, InstanceType.ProcessingNode, machineName)
            .ShouldBe(Path.Combine("./logs", expected));
    }

    /// <summary>One web server and one thumbnail generator per deployment: their names stay as they are.</summary>
    [TestCase(InstanceType.WebServer, "registry-web.txt")]
    [TestCase(InstanceType.ThumbnailGenerator, "registry-thumbs.txt")]
    [TestCase(InstanceType.Default, "registry.txt")]
    public void OtherInstanceTypes_IgnoreTheMachineName(InstanceType instanceType, string expected)
    {
        InstanceLogPaths.PathFor(ConfiguredLogPath, instanceType, "registry-node-1")
            .ShouldBe(instanceType == InstanceType.Default ? ConfiguredLogPath : Path.Combine("./logs", expected));
    }
}
