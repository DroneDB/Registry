#nullable enable
using System;
using System.IO;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-02 item 7: <c>Main</c> returned <c>void</c>, so every failure (unparsable command line, invalid
/// address, settings or DroneDB failure, a host that throws, the thumbnail generator refusing to start)
/// exited with code 0, and the documented <c>--check</c> option was never read: it set up the storage
/// folder (writing appsettings.json, temp, Hub) and started the host. <c>Main</c> now returns the code
/// of <c>Program.Run</c> (0 success, 1 failure, 2 invalid arguments) and <c>--check</c> verifies the
/// settings read-only, without a host.
/// Baseline before the fix (built binary): <c>--bogus</c>, <c>-a bad:1:2</c> and <c>--check -t 1</c>
/// on a new folder (processing node failing on the in-memory Hangfire provider) all exited 0, the last
/// after writing appsettings.json and temp.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~Rq02ExitCodeTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
[NonParallelizable]
[Category(TestCategories.Native)]
public class Rq02ExitCodeTests
{
    private CommandLineHarness _harness = null!;
    private int _hostStarts;

    [SetUp]
    public void SetUp()
    {
        _harness = new CommandLineHarness();
        _hostStarts = 0;
    }

    [TearDown]
    public void TearDown() => _harness.Dispose();

    /// <summary>The command line without starting a host (counts the attempts) and without a password.</summary>
    private (int ExitCode, string Out, string Error) Run(params string[] args) =>
        CommandLineHarness.Capture(() => Program.Run(args, _ =>
        {
            _hostStarts++;
            return ExitCodes.Success;
        }, () => null));

    [TestCase("--bogus")]
    [TestCase("--instance-type", "7x")]
    public void Run_UnparsableCommandLine_ReturnsInvalidArguments(params string[] args)
    {
        Run(args).ExitCode.ShouldBe(ExitCodes.InvalidArguments);
        _hostStarts.ShouldBe(0);
    }

    [Test]
    public void Run_NoStorageFolder_ReturnsInvalidArguments()
    {
        Run().ExitCode.ShouldBe(ExitCodes.InvalidArguments);
    }

    [TestCase("--help")]
    [TestCase("--version")]
    public void Run_HelpOrVersion_ReturnsSuccess(string option)
    {
        Run(option).ExitCode.ShouldBe(ExitCodes.Success);
        _hostStarts.ShouldBe(0);
    }

    [Test]
    public void Run_InvalidAddress_ReturnsInvalidArgumentsBeforeTouchingTheFolder()
    {
        Run(_harness.StorageFolder, "--address", "bad:1:2").ExitCode.ShouldBe(ExitCodes.InvalidArguments);

        _hostStarts.ShouldBe(0);
        _harness.Snapshot().ShouldBeEmpty();
    }

    /// <summary>A first-start folder (default settings) is valid; nothing is written, no host starts.</summary>
    [Test]
    public void Check_ValidSettings_ReturnsSuccessAndWritesNothing()
    {
        _harness.WriteSettings(CommandLineHarness.DefaultSettings());
        var before = _harness.Snapshot();
        var currentDirectory = Environment.CurrentDirectory;

        var (exitCode, output, error) = Run(_harness.StorageFolder, "--check");

        exitCode.ShouldBe(ExitCodes.Success, error);
        output.ShouldContain("Configuration is valid");
        _hostStarts.ShouldBe(0);
        _harness.Snapshot().ShouldBe(before);
        Environment.CurrentDirectory.ShouldBe(currentDirectory);
    }

    [Test]
    public void Check_InvalidSettings_ReturnsFailureWithTheReasonOnStderr()
    {
        var settings = CommandLineHarness.DefaultSettings();
        settings["AppSettings"]!["TokenExpirationInDays"] = 0;
        _harness.WriteSettings(settings);
        var before = _harness.Snapshot();

        var (exitCode, _, error) = Run(_harness.StorageFolder, "--check");

        exitCode.ShouldBe(ExitCodes.Failure);
        error.ShouldContain("TokenExpirationInDays");
        _hostStarts.ShouldBe(0);
        _harness.Snapshot().ShouldBe(before);
    }

    [Test]
    public void Check_UnreadableSettings_ReturnsFailure()
    {
        File.WriteAllText(_harness.SettingsPath, "{ not json");

        Run(_harness.StorageFolder, "--check").ExitCode.ShouldBe(ExitCodes.Failure);
        _hostStarts.ShouldBe(0);
    }

    /// <summary>--check never creates the default settings (a normal start would).</summary>
    [Test]
    public void Check_NoSettingsFile_ReturnsFailureAndCreatesNothing()
    {
        var (exitCode, _, error) = Run(_harness.StorageFolder, "--check");

        exitCode.ShouldBe(ExitCodes.Failure);
        error.ShouldContain("appsettings.json");
        _harness.Snapshot().ShouldBeEmpty();
    }

    [Test]
    public void RunInstance_HostThrows_ReturnsFailure()
    {
        var (exitCode, _, error) = CommandLineHarness.Capture(() =>
            Program.RunInstance(new Options(), _ => throw new InvalidOperationException("host exploded")));

        exitCode.ShouldBe(ExitCodes.Failure);
        error.ShouldContain("host exploded");
    }

    [TestCase(ExitCodes.Success)]
    [TestCase(ExitCodes.Failure)]
    public void RunInstance_HostReturns_ItsCodeIsTheExitCode(int hostExitCode)
    {
        CommandLineHarness.Capture(() => Program.RunInstance(new Options(), _ => hostExitCode))
            .ExitCode.ShouldBe(hostExitCode);
    }

    /// <summary>
    /// The whole run path of a processing node (DroneDB init, storage folder setup) up to a host that
    /// throws: the case that used to print the error and exit 0.
    /// </summary>
    [Test]
    public void Run_ProcessingNodeHostThrows_ReturnsFailure()
    {
        var (exitCode, _, error) = CommandLineHarness.Capture(() => Program.Run(
            [_harness.StorageFolder, "--instance-type", "1"],
            _ => throw new NotSupportedException("in-memory Hangfire on a processing node"), () => null));

        error.ShouldContain("in-memory Hangfire on a processing node");
        exitCode.ShouldBe(ExitCodes.Failure);
    }
}
