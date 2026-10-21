#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Serilog;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-02 item 10 (logging): the Serilog File sink had only a <c>path</c>, so it never rolled, kept no
/// retention and stopped writing at the 1 GB default size limit; three containers (web server and two
/// processing nodes) wrote the same file without <c>shared</c>. The shipped configurations
/// (<c>appsettings-default.json</c>, embedded in Registry.Web, and the docker production/testing
/// templates) now roll daily and on size, keep 14 files and open the file shared.
/// Every host reads these settings through the one <c>Program.ConfigureSerilog</c>.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq02LoggingConfigTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq02LoggingConfigTests
{
    private const string DefaultSettingsResourceSuffix = "appsettings-default.json";

    /// <summary>The File sink arguments every shipped configuration must carry.</summary>
    private static readonly Dictionary<string, JToken> ExpectedFileSinkArgs = new()
    {
        ["rollingInterval"] = "Day",
        ["retainedFileCountLimit"] = 14,
        ["fileSizeLimitBytes"] = 104857600,
        ["rollOnFileSizeLimit"] = true,
        ["shared"] = true
    };

    private static string ReadDefaultSettings()
    {
        var assembly = typeof(Program).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(DefaultSettingsResourceSuffix, StringComparison.Ordinal));

        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    private static JObject FileSinkArgs(string json)
    {
        var sink = JObject.Parse(json)["Serilog"]!["WriteTo"]!.Children<JObject>()
            .Single(s => (string?)s["Name"] == "File");
        return (JObject)sink["Args"]!;
    }

    private static void ShouldHaveRollingSharedArgs(JObject args, string source)
    {
        ((string?)args["path"]).ShouldBe("./logs/registry.txt", source);
        foreach (var (key, expected) in ExpectedFileSinkArgs)
            JToken.DeepEquals(args[key], expected).ShouldBeTrue($"{source}: {key} = {args[key]}, expected {expected}");
    }

    /// <summary>Walks up from the test output folder to the repository root (holds Registry.sln).</summary>
    private static string? FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Registry.sln")))
                return dir.FullName;
        return null;
    }

    /// <summary>The embedded default (what a new storage folder's appsettings.json is created from).</summary>
    [Test]
    public void DefaultSettings_FileSink_RollsRetainsAndIsShared()
    {
        ShouldHaveRollingSharedArgs(FileSinkArgs(ReadDefaultSettings()), DefaultSettingsResourceSuffix);
    }

    /// <summary>The docker templates (production run.sh renders appsettings.json from its template) agree.</summary>
    [TestCase("docker/production/appsettings-template.json")]
    [TestCase("docker/testing/appsettings-testing.json")]
    public void DockerTemplates_FileSink_MatchTheDefault(string relativePath)
    {
        var root = FindRepositoryRoot();
        Assume.That(root, Is.Not.Null, "repository sources not available next to the test output");

        ShouldHaveRollingSharedArgs(FileSinkArgs(File.ReadAllText(Path.Combine(root!, relativePath))), relativePath);
    }

    /// <summary>
    /// The arguments bind (a typo such as <c>"Daily"</c> would throw): a logger built from the default
    /// Serilog section writes to the day-stamped file, e.g. <c>registry20261020.txt</c>.
    /// </summary>
    [Test]
    public void DefaultSettings_FileSink_WritesTheDayStampedFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rq02-logging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ReadDefaultSettings())))
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // WriteTo:0 is Console, WriteTo:1 the File sink (same index RegistryWebFactory overrides)
                    ["Serilog:WriteTo:1:Args:path"] = Path.Combine(folder, "registry.txt")
                })
                .Build();

            using (var logger = new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger())
                logger.Warning("rq02 rolling file sink");

            var written = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
            written.ShouldHaveSingleItem().ShouldMatch(@"^registry\d{8}\.txt$");
            File.ReadAllText(Path.Combine(folder, written[0]!)).ShouldContain("rq02 rolling file sink");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
