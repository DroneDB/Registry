using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The storage folder's settings file, through the command line (<see cref="Program.Run"/>, no host):
/// <list type="bullet">
/// <item>a normal start no longer rewrites <c>appsettings.json</c> (it serialized the whole <c>AppSettings</c>
/// back on every start of every container, dropping the keys it did not know);</item>
/// <item>a missing secret is generated and written, and nothing else of the file changes;</item>
/// <item><c>--check</c> validates what a host reads (the file and the environment) with the rules of the
/// role given by <c>--instance-type</c>.</item>
/// </list>
/// The first three tests are red when the file is rewritten, an unknown key dropped, or the environment and
/// the role ignored by <c>--check</c>.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~SettingsFileTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
[NonParallelizable]
[Category(TestCategories.Native)]
public class SettingsFileTests
{
    private const string ProcessingNode = "1";

    private int _hostStarts;

    [SetUp]
    public void SetUp() => _hostStarts = 0;

    private (int ExitCode, string Out, string Error) Run(params string[] args) =>
        CommandLineHarness.Capture(() => Program.Run(args, _ =>
        {
            _hostStarts++;
            return ExitCodes.Success;
        }, () => null!));

    private static JObject CompleteSettings()
    {
        var settings = CommandLineHarness.DefaultSettings();
        settings["AppSettings"]!["Secret"] = "settingsfile-secret-0123456789";
        settings["AppSettings"]!["ThumbnailGeneratorToken"] = "settingsfile-thumbnail-token";
        settings["AppSettings"]!["HangfireProvider"] = "Mysql";
        // A key this version does not know (the production template used to carry ChunkedUploadSessionTimeout, which nothing
        // read: the files of existing deployments still have it)
        settings["AppSettings"]!["ChunkedUploadSessionTimeout"] = "01:00:00";
        return settings;
    }

    [Test]
    public void NormalStart_DoesNotWriteTheSettingsFile()
    {
        using var harness = new CommandLineHarness();
        harness.WriteSettings(CompleteSettings());
        var before = File.ReadAllBytes(harness.SettingsPath);

        var (exitCode, _, error) = Run(harness.StorageFolder, "--instance-type", ProcessingNode);

        exitCode.ShouldBe(ExitCodes.Success, error);
        _hostStarts.ShouldBe(1);
        File.ReadAllBytes(harness.SettingsPath).ShouldBe(before, "a start with every secret set must not rewrite the file");
    }

    [Test]
    public void MissingSecrets_AreGenerated_AndTheRestOfTheFileIsKept()
    {
        using var harness = new CommandLineHarness();
        var settings = CompleteSettings();
        settings["AppSettings"]!["Secret"] = "";
        ((JObject)settings["AppSettings"]!).Remove("ThumbnailGeneratorToken");
        harness.WriteSettings(settings);

        var (exitCode, _, error) = Run(harness.StorageFolder, "--instance-type", ProcessingNode);

        exitCode.ShouldBe(ExitCodes.Success, error);
        var written = JObject.Parse(File.ReadAllText(harness.SettingsPath));
        written["AppSettings"]!.Value<string>("Secret")!.Length.ShouldBe(64);
        written["AppSettings"]!.Value<string>("ThumbnailGeneratorToken")!.Length.ShouldBe(64);
        written["AppSettings"]!.Value<string>("ChunkedUploadSessionTimeout").ShouldBe("01:00:00",
            "a key the code does not know is the operator's: it stays");

        // Nothing else changed: the file is the original plus the two secrets
        ((JObject)written["AppSettings"]!).Remove("Secret");
        ((JObject)written["AppSettings"]!).Remove("ThumbnailGeneratorToken");
        ((JObject)settings["AppSettings"]!).Remove("Secret");
        JToken.DeepEquals(written, settings).ShouldBeTrue();
    }

    [Test]
    public void Check_SeesTheEnvironment_AsTheHostDoes()
    {
        using var harness = new CommandLineHarness();
        var settings = CompleteSettings();
        settings["AppSettings"]!["TokenExpirationInDays"] = 0;
        harness.WriteSettings(settings);

        try
        {
            Environment.SetEnvironmentVariable("AppSettings__TokenExpirationInDays", "7");

            var (exitCode, output, error) = Run(harness.StorageFolder, "--check");

            exitCode.ShouldBe(ExitCodes.Success, error);
            output.ShouldContain("Configuration is valid");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AppSettings__TokenExpirationInDays", null);
        }
    }

    [Test]
    public void Check_AppliesTheRulesOfTheInstanceType()
    {
        using var harness = new CommandLineHarness();
        // The defaults keep the jobs in memory: fine for a web server, not for a processing node
        var settings = CommandLineHarness.DefaultSettings();
        harness.WriteSettings(settings);

        Run(harness.StorageFolder, "--check").ExitCode.ShouldBe(ExitCodes.Success);

        var (exitCode, _, error) = Run(harness.StorageFolder, "--check", "--instance-type", ProcessingNode);
        exitCode.ShouldBe(ExitCodes.Failure);
        error.ShouldContain("InMemory is not supported for a processing node");
        _hostStarts.ShouldBe(0);
    }
}
