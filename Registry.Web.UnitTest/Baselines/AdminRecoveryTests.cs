using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>DefaultAdmin.Password</c> no longer resets an existing admin on start,
/// so a single-admin instance that lost its password had no recovery path. <c>--reset-admin-password</c>
/// sets a new password for the configured default admin through the web host's <c>UserManager</c>
/// (password policy applies), against the configured identity database, without starting the host.
/// The password comes from <c>REGISTRY_ADMIN_PASSWORD</c> or stdin, never from the command line.
/// Baseline before the fix: the option did not exist (the command line failed to parse, exit 0).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~AdminRecoveryTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
[NonParallelizable]
public class AdminRecoveryTests
{
    private const string NewPassword = "Recovered-Passw0rd";

    private RegistryWebFactory _factory = null!;
    private CommandLineHarness _harness = null!;

    [SetUp]
    public void SetUp()
    {
        // The factory host seeds the default admin (password123) in a temp SQLite identity database
        _factory = new RegistryWebFactory();
        _ = _factory.Services;
        _harness = new CommandLineHarness();
    }

    [TearDown]
    public void TearDown()
    {
        _harness.Dispose();
        _factory.Dispose();
    }

    private async Task<(bool OldValid, bool NewValid)> CheckAdminPasswordsAsync(string newPassword)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = await userManager.FindByNameAsync(RegistryWebFactory.AdminUserName);
        admin.ShouldNotBeNull();

        return (await userManager.CheckPasswordAsync(admin, RegistryWebFactory.AdminPassword),
            await userManager.CheckPasswordAsync(admin, newPassword));
    }

    /// <summary>
    /// A storage folder whose settings point at the factory's identity and registry databases
    /// (absolute paths), as an operator's appsettings.json points at the production ones.
    /// </summary>
    private void WriteSettingsForTheFactoryDatabases()
    {
        var settings = CommandLineHarness.DefaultSettings();
        settings["ConnectionStrings"] = new JObject
        {
            ["IdentityConnection"] = $"Data Source={Path.Combine(_factory.RootPath, "identity.db")};Mode=ReadWriteCreate",
            ["RegistryConnection"] = $"Data Source={Path.Combine(_factory.RootPath, "registry.db")};Mode=ReadWriteCreate"
        };
        _harness.WriteSettings(settings);
    }

    [Test]
    public async Task ResetDefaultAdminPassword_SeededAdmin_NewPasswordIsValid()
    {
        var exitCode = CommandLineHarness.Capture(() =>
        {
            using var log = Hosting.RegistryHost.CreateCommandLineLogger();
            return Program.ResetDefaultAdminPasswordAsync(_factory.Services, NewPassword, log).GetAwaiter().GetResult();
        }).ExitCode;

        exitCode.ShouldBe(ExitCodes.Success);
        (await CheckAdminPasswordsAsync(NewPassword)).ShouldBe((false, true));
    }

    /// <summary>The password policy of the instance applies (default: 8 characters with a digit).</summary>
    [Test]
    public async Task ResetDefaultAdminPassword_PasswordAgainstThePolicy_ReturnsFailureAndKeepsTheOldOne()
    {
        var (exitCode, _, error) = CommandLineHarness.Capture(() =>
        {
            using var log = Hosting.RegistryHost.CreateCommandLineLogger();
            return Program.ResetDefaultAdminPasswordAsync(_factory.Services, "short", log).GetAwaiter().GetResult();
        });

        exitCode.ShouldBe(ExitCodes.Failure);
        error.ShouldContain("Password not changed");
        (await CheckAdminPasswordsAsync("short")).ShouldBe((true, false));
    }

    /// <summary>End to end: the command line builds the host from the storage folder's settings.</summary>
    [Test]
    public async Task Run_ResetAdminPassword_ChangesThePasswordWithoutStartingAHost()
    {
        WriteSettingsForTheFactoryDatabases();
        var hostStarts = 0;

        var (exitCode, output, error) = CommandLineHarness.Capture(() => Program.Run(
            [_harness.StorageFolder, "--reset-admin-password"],
            _ => ++hostStarts, () => NewPassword));

        exitCode.ShouldBe(ExitCodes.Success, error);
        output.ShouldContain("changed");
        hostStarts.ShouldBe(0);
        (await CheckAdminPasswordsAsync(NewPassword)).ShouldBe((false, true));
    }

    [Test]
    public async Task Run_ResetAdminPassword_NoPassword_ReturnsFailure()
    {
        WriteSettingsForTheFactoryDatabases();

        var (exitCode, _, error) = CommandLineHarness.Capture(() => Program.Run(
            [_harness.StorageFolder, "--reset-admin-password"], _ => ExitCodes.Success, () => null));

        exitCode.ShouldBe(ExitCodes.Failure);
        error.ShouldContain(Program.AdminPasswordEnvironmentVariable);
        (await CheckAdminPasswordsAsync(NewPassword)).ShouldBe((true, false));
    }

    /// <summary>The environment variable wins; stdin is read only without it; neither = no password.</summary>
    [TestCase("from-env", "from-stdin", ExpectedResult = "from-env")]
    [TestCase(null, "from-stdin", ExpectedResult = "from-stdin")]
    [TestCase("", "from-stdin", ExpectedResult = "from-stdin")]
    [TestCase(null, null, ExpectedResult = null)]
    [TestCase(null, "", ExpectedResult = null)]
    public string? ReadAdminPassword_EnvironmentThenStdin(string? environment, string? stdin)
    {
        var variables = new Dictionary<string, string?> { [Program.AdminPasswordEnvironmentVariable] = environment };
        return Program.ReadAdminPassword(name => variables.GetValueOrDefault(name)!, () => stdin!);
    }
}
