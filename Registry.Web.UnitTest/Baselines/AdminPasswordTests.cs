#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsOptions = Microsoft.Extensions.Options.Options;
using NUnit.Framework;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Initialization;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The default admin's password (one change: dropping only the appsettings write would let the
/// per-start reset undo the admin's own password change):
/// <list type="bullet">
/// <item>Changing the default admin's password no longer writes it to appsettings.json nor
/// mutates the singleton <c>IOptions&lt;AppSettings&gt;.Value</c>; the start-up banner no longer prints
/// the admin password nor the MonitorToken.</item>
/// <item><c>DatabaseInitializer</c> seeds the configured password only when it creates the
/// admin; on later starts it keeps the stored password and security stamp (no logout), and a changed
/// admin email in the configuration is saved through the identity store (it was saved on the wrong
/// context and lost).</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~AdminPasswordTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class AdminPasswordTests
{
    private const string NewPassword = "New-Admin-Pass-2026!";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task ChangeAdminPasswordAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/changepwd")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["oldPassword"] = RegistryWebFactory.AdminPassword,
                ["newPassword"] = NewPassword
            })
        }.AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);

        using var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<User> FindAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return (await userManager.FindByNameAsync(RegistryWebFactory.AdminUserName))!;
    }

    private async Task<bool> AdminPasswordIsAsync(string password)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = await userManager.FindByNameAsync(RegistryWebFactory.AdminUserName);
        return await userManager.CheckPasswordAsync(admin!, password);
    }

    /// <summary>Runs the start-up database initialization again (what every restart does).</summary>
    private async Task RunDatabaseInitializerAsync(IdentityProviderOptions? settingsOverride = null)
    {
        using var scope = _factory.Services.CreateScope();
        var admin = settingsOverride == null
            ? ActivatorUtilities.CreateInstance<DefaultAdminProvisioner>(scope.ServiceProvider)
            : ActivatorUtilities.CreateInstance<DefaultAdminProvisioner>(scope.ServiceProvider, MsOptions.Create(settingsOverride));
        var initializer = ActivatorUtilities.CreateInstance<DatabaseInitializer>(scope.ServiceProvider, admin);

        await initializer.ExecuteAsync(CancellationToken.None);
    }

    [Test]
    public async Task ChangedAdminPassword_SurvivesRestart_AndKeepsSecurityStamp()
    {
        await ChangeAdminPasswordAsync();
        var stampAfterChange = (await FindAdminAsync()).SecurityStamp;

        await RunDatabaseInitializerAsync();

        (await AdminPasswordIsAsync(NewPassword)).ShouldBeTrue("the admin's own password change must survive a restart");
        (await AdminPasswordIsAsync(RegistryWebFactory.AdminPassword)).ShouldBeFalse();
        (await FindAdminAsync()).SecurityStamp.ShouldBe(stampAfterChange,
            "a restart must not rotate the security stamp (it logs the admin out)");
    }

    [Test]
    public async Task UnchangedAdmin_RestartKeepsSecurityStamp()
    {
        var stampBefore = (await FindAdminAsync()).SecurityStamp;

        await RunDatabaseInitializerAsync();

        (await FindAdminAsync()).SecurityStamp.ShouldBe(stampBefore);
        (await AdminPasswordIsAsync(RegistryWebFactory.AdminPassword)).ShouldBeTrue();
    }

    /// <summary>
    /// The appsettings.json files the host could write: relative to the process directory (where the
    /// removed ConfigurationHelper wrote it), in the content root and next to the binaries.
    /// Maps each full path to its content, or null when the file does not exist.
    /// </summary>
    private Dictionary<string, byte[]?> SnapshotAppSettingsFiles() =>
        new[]
            {
                Path.GetFullPath(MagicStrings.AppSettingsFileName),
                Path.Combine(_factory.RootPath, MagicStrings.AppSettingsFileName),
                Path.Combine(AppContext.BaseDirectory, MagicStrings.AppSettingsFileName)
            }
            .Select(Path.GetFullPath)
            .Distinct()
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);

    [Test]
    public async Task ChangedAdminPassword_IsNotPersistedToConfiguration()
    {
        var before = SnapshotAppSettingsFiles();

        await ChangeAdminPasswordAsync();

        var after = SnapshotAppSettingsFiles();
        foreach (var (path, content) in before)
            after[path].ShouldBe(content, $"the admin password must not be written to {path}");
        _factory.Services.GetRequiredService<IOptions<IdentityProviderOptions>>().Value.DefaultAdmin!.Password
            .ShouldBe(RegistryWebFactory.AdminPassword, "a request must not mutate the singleton settings");
    }

    [Test]
    public async Task ChangedAdminEmailInConfiguration_IsPersistedOnRestart()
    {
        const string newEmail = "adminpassword-admin@example.org";
        var stampBefore = (await FindAdminAsync()).SecurityStamp;
        var configured = new IdentityProviderOptions
        {
            DefaultAdmin = new AdminInfo
            {
                UserName = RegistryWebFactory.AdminUserName,
                Password = RegistryWebFactory.AdminPassword,
                Email = newEmail
            }
        };

        await RunDatabaseInitializerAsync(configured);

        var admin = await FindAdminAsync();
        admin.Email.ShouldBe(newEmail);
        admin.NormalizedEmail.ShouldBe(newEmail.ToUpperInvariant());
        admin.SecurityStamp.ShouldBe(stampBefore);
    }

    [Test]
    public void StartupInfo_DoesNotPrintAdminPasswordOrMonitorToken()
    {
        const string password = "Startup-Admin-Secret";
        const string monitorToken = "adminpassword-startup-monitor-token-42";
        var identity = new IdentityProviderOptions
        {
            DefaultAdmin = new AdminInfo { UserName = "admin", Password = password, Email = "admin@example.com" }
        };
        // The banner's inputs are the HTTP and identity options; the monitor token is in neither
        var http = new HttpHostOptions { ExternalUrlOverride = null };

        var text = string.Join(Environment.NewLine,
            Hosting.StartupBanner.Lines("Production", ["http://0.0.0.0:5000"], http, identity));
        text.ShouldNotContain(password);
        text.ShouldNotContain(monitorToken);
        // The rest of the banner stays
        text.ShouldContain("Registry url: http://localhost:5000");
        text.ShouldContain("Quick Health: http://localhost:5000/quickhealth");
        text.ShouldContain("Press Ctrl+C to quit");
    }
}
