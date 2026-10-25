#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Services.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The update check as the Hub sees it: <c>GET /sys/update-info</c>, <c>PUT /sys/update-check/settings</c>, the anonymous
/// features, and the "Update check" section of the configuration view.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateCheckEndpointTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public class UpdateCheckEndpointTests : IDisposable
{
    private const string InfoUrl = "/sys/update-info";
    private const string SettingsUrl = "/sys/update-check/settings";
    private const string RegularUser = "update-regular-user";

    private RegistryWebFactory _factory = null!;
    private HttpClient? _client;

    private void Start(Dictionary<string, string>? settings = null, Action<IServiceCollection>? services = null)
    {
        _factory = new RegistryWebFactory(settings, services);
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        _factory?.Dispose();
        _factory = null!;
        GC.SuppressFinalize(this);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null, string? user = RegistryWebFactory.AdminUserName,
        bool isAdmin = true)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null)
            request.Content = JsonContent.Create(body);
        if (user != null)
            request.AsUser(user, isAdmin);
        return await _client!.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private int QueuedChecks() => _factory.Services.GetRequiredService<JobStorage>().GetMonitoringApi()
        .EnqueuedJobs("default", 0, 100).Count(j => j.Value.Job?.Type == typeof(IUpdateCheckService));

    // ---- authorization

    // An anonymous caller meets the authentication challenge, as on every endpoint behind [Authorize]: a bare 401
    [TestCase("GET", InfoUrl)]
    [TestCase("PUT", SettingsUrl)]
    public async Task Anonymous_IsUnauthorized(string method, string url)
    {
        Start();

        using var response = await Send(new HttpMethod(method), url, method == "PUT" ? new { useDeclaration = "personal" } : null, user: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        QueuedChecks().ShouldBe(0);
        File.Exists(Path.Combine(_factory.StoragePath, MagicStrings.UpdateCheckSettingsFileName)).ShouldBeFalse();
    }

    [TestCase("GET", InfoUrl)]
    [TestCase("PUT", SettingsUrl)]
    public async Task ARegularUser_IsForbidden_WithProblemDetails(string method, string url)
    {
        Start();
        await _factory.CreateUserAsync(RegularUser);

        using var response = await Send(new HttpMethod(method), url, method == "PUT" ? new { useDeclaration = "personal" } : null,
            RegularUser, isAdmin: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await Json(response)).GetProperty("status").GetInt32().ShouldBe(403);
        QueuedChecks().ShouldBe(0);
        File.Exists(Path.Combine(_factory.StoragePath, MagicStrings.UpdateCheckSettingsFileName)).ShouldBeFalse();
    }

    // ---- GET /sys/update-info

    [Test]
    public async Task UpdateInfo_BeforeTheFirstCheck_HasNoVersions_ButTheLicenceLink()
    {
        Start();

        using var response = await Send(HttpMethod.Get, InfoUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var info = await Json(response);
        info.EnumerateObject().Select(p => p.Name).ShouldBe(
        [
            "instanceId", "currentVersion", "latestVersion", "isUpdateAvailable", "isSecurityUpdate", "releaseDate", "changelogUrl",
            "compareUrl", "releaseNotes", "licenseRequestUrl", "useDeclaration", "contactEmail", "lastChecked"
        ], ignoreOrder: true);
        info.GetProperty("latestVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        info.GetProperty("releaseNotes").ValueKind.ShouldBe(JsonValueKind.Null);
        info.GetProperty("lastChecked").ValueKind.ShouldBe(JsonValueKind.Null);
        info.GetProperty("isUpdateAvailable").GetBoolean().ShouldBeFalse();
        info.GetProperty("currentVersion").GetString().ShouldNotBeNullOrEmpty();
        Guid.TryParse(info.GetProperty("instanceId").GetString(), out _).ShouldBeTrue();
        info.GetProperty("licenseRequestUrl").GetString().ShouldStartWith("https://dronedb.app/request-license?instance=");
    }

    [Test]
    public async Task UpdateInfo_AfterACheck_HasTheVersionsAndTheMergedNotes()
    {
        var answer = UpdateCheckTestKit.Answer("99.0.0", "## Security\n- Closed a hole\n\n## Fixed\n- A bug");
        Start(null, services => services.AddHttpClient(UpdateCheckClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK, answer)));
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IUpdateCheckService>().CheckForUpdatesAsync(default);

        using var response = await Send(HttpMethod.Get, InfoUrl);

        var info = await Json(response);
        info.GetProperty("latestVersion").GetString().ShouldBe("99.0.0");
        info.GetProperty("isUpdateAvailable").GetBoolean().ShouldBeTrue();
        info.GetProperty("isSecurityUpdate").GetBoolean().ShouldBeTrue();
        info.GetProperty("releaseNotes").GetString().ShouldStartWith("## Security\n- Closed a hole (v99.0.0)");
        info.GetProperty("compareUrl").GetString()!.ShouldContain("/compare/");
        info.GetProperty("lastChecked").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Test]
    public async Task TheLicenceLink_CarriesTheInstanceTheUrlAndTheVersion_Escaped()
    {
        Start(new Dictionary<string, string> { ["AppSettings:ExternalUrlOverride"] = "https://hub.example.org/a?b=1&c=d#x" });

        var info = await Json(await Send(HttpMethod.Get, InfoUrl));

        var link = new Uri(info.GetProperty("licenseRequestUrl").GetString()!);
        link.GetLeftPart(UriPartial.Path).ShouldBe("https://dronedb.app/request-license");
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        query["instance"].ShouldBe(info.GetProperty("instanceId").GetString());
        query["url"].ShouldBe("https://hub.example.org/a?b=1&c=d#x");
        query["version"].ShouldBe(info.GetProperty("currentVersion").GetString());
        link.Query.ShouldContain("url=https%3A%2F%2Fhub.example.org%2Fa%3Fb%3D1%26c%3Dd%23x");
    }

    // ---- PUT /sys/update-check/settings

    [Test]
    public async Task Put_SavesTheSettings_AnswersTheUpdateInfo_AndQueuesOneCheck()
    {
        Start();

        using var response = await Send(HttpMethod.Put, SettingsUrl,
            new { useDeclaration = "commercial_needs_license", contactEmail = "it@acme-survey.com", contactConsent = true });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var info = await Json(response);
        info.GetProperty("useDeclaration").GetString().ShouldBe("commercial_needs_license");
        info.GetProperty("contactEmail").GetString().ShouldBe("it@acme-survey.com");
        info.GetProperty("instanceId").GetString().ShouldNotBeNullOrEmpty();
        QueuedChecks().ShouldBe(1);

        // What the next check sends
        var settings = await File.ReadAllTextAsync(Path.Combine(_factory.StoragePath, MagicStrings.UpdateCheckSettingsFileName));
        settings.ShouldContain("commercial_needs_license");
        settings.ShouldContain("contactConsentAt");
    }

    [Test]
    public async Task Put_AgainWithinTenMinutes_QueuesNoSecondCheck()
    {
        Start();
        (await Send(HttpMethod.Put, SettingsUrl, new { useDeclaration = "personal" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await Send(HttpMethod.Put, SettingsUrl, new { useDeclaration = "nonprofit" });

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(second)).GetProperty("useDeclaration").GetString().ShouldBe("nonprofit");
        QueuedChecks().ShouldBe(1);
    }

    [Test]
    public async Task Put_WhenTheCheckIsDisabled_SavesButQueuesNothing()
    {
        Start(new Dictionary<string, string> { ["AppSettings:UpdateCheckCron"] = "disabled" });

        using var response = await Send(HttpMethod.Put, SettingsUrl, new { useDeclaration = "personal" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(response)).GetProperty("useDeclaration").GetString().ShouldBe("personal");
        QueuedChecks().ShouldBe(0);
    }

    [Test]
    public async Task Put_AnEmailWithoutConsent_IsA400_ProblemDetails_AndSavesNothing()
    {
        Start();

        using var response = await Send(HttpMethod.Put, SettingsUrl, new { contactEmail = "it@acme-survey.com", contactConsent = false });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        (await Json(response)).GetProperty("detail").GetString()!.ShouldContain("contactConsent");
        File.Exists(Path.Combine(_factory.StoragePath, MagicStrings.UpdateCheckSettingsFileName)).ShouldBeFalse();
        QueuedChecks().ShouldBe(0);
    }

    [Test]
    public async Task Put_AnUnknownDeclaration_IsA400()
    {
        Start();

        using var response = await Send(HttpMethod.Put, SettingsUrl, new { useDeclaration = "hobbyist" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("detail").GetString()!.ShouldContain("useDeclaration");
    }

    [Test]
    public async Task Put_AnEmptyEmail_ClearsTheContact()
    {
        Start();
        (await Send(HttpMethod.Put, SettingsUrl, new { contactEmail = "it@acme-survey.com", contactConsent = true })).EnsureSuccessStatusCode();

        using var response = await Send(HttpMethod.Put, SettingsUrl, new { contactEmail = "", contactConsent = false });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(response)).GetProperty("contactEmail").ValueKind.ShouldBe(JsonValueKind.Null);
        var file = await File.ReadAllTextAsync(Path.Combine(_factory.StoragePath, MagicStrings.UpdateCheckSettingsFileName));
        JsonDocument.Parse(file).RootElement.GetProperty("contactConsentAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task Put_WithAnEmptyBody_IsA400()
    {
        Start();
        using var request = new HttpRequestMessage(HttpMethod.Put, SettingsUrl)
        {
            Content = new StringContent("", System.Text.Encoding.UTF8, "application/json")
        };
        request.AsUser(RegistryWebFactory.AdminUserName, true);

        using var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- the anonymous features and the configuration view

    [Test]
    public async Task Features_TellAnonymousClients_WhetherTheCheckRuns_AndTheInstanceHash()
    {
        Start();

        using var response = await Send(HttpMethod.Get, "/sys/features", user: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var features = await Json(response);
        features.GetProperty("updateCheckEnabled").GetBoolean().ShouldBeTrue();
        var hash = features.GetProperty("instanceHash").GetString()!;
        hash.ShouldMatch("^[0-9a-f]{64}$");

        var info = await Json(await Send(HttpMethod.Get, InfoUrl));
        InstanceIdentity.HashOf(info.GetProperty("instanceId").GetString()!).ShouldBe(hash);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(info.GetProperty("instanceId").GetString()!);
    }

    [Test]
    public async Task TheFeaturesHash_IsTheSha256_OfThePrefixedLowercaseInstanceId_ComputedHere()
    {
        Start();

        var info = await Json(await Send(HttpMethod.Get, InfoUrl));
        var id = info.GetProperty("instanceId").GetString()!;
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("ddb-instance-v1:" + id.ToLowerInvariant()))).ToLowerInvariant();

        var features = await Json(await Send(HttpMethod.Get, "/sys/features", user: null));

        features.GetProperty("instanceHash").GetString().ShouldBe(expected);
    }

    [TestCase("disabled")]
    [TestCase("off")]
    [TestCase("none")]
    public async Task Features_SayTheCheckIsOff_WhenItIsDisabled(string cron)
    {
        Start(new Dictionary<string, string> { ["AppSettings:UpdateCheckCron"] = cron });

        var features = await Json(await Send(HttpMethod.Get, "/sys/features", user: null));

        features.GetProperty("updateCheckEnabled").GetBoolean().ShouldBeFalse();
        features.GetProperty("instanceHash").GetString().ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task TheInstanceIdentity_IsTheSame_AfterARestartOnTheSameStorage()
    {
        var storage = Directory.CreateTempSubdirectory("registry-identity-").FullName;
        try
        {
            var settings = new Dictionary<string, string> { ["AppSettings:StoragePath"] = storage };
            Start(settings);
            var first = (await Json(await Send(HttpMethod.Get, InfoUrl))).GetProperty("instanceId").GetString();
            Dispose();

            Start(settings);

            (await Json(await Send(HttpMethod.Get, InfoUrl))).GetProperty("instanceId").GetString().ShouldBe(first);
            File.ReadAllText(Path.Combine(storage, MagicStrings.InstanceIdFileName)).ShouldContain(first!);
        }
        finally
        {
            Dispose();
            Directory.Delete(storage, true);
        }
    }

    private async Task<JsonElement> UpdateCheckSection()
    {
        using var response = await Send(HttpMethod.Get, "/sys/config");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await Json(response)).GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "UpdateCheck");
    }

    [Test]
    public async Task ConfigView_ShowsTheDisclosure_WithTheInstanceId()
    {
        Start();

        var section = await UpdateCheckSection();

        var instanceId = (await Json(await Send(HttpMethod.Get, InfoUrl))).GetProperty("instanceId").GetString()!;
        section.GetProperty("title").GetString().ShouldBe("Update check");
        section.GetProperty("description").GetString().ShouldBe(UpdateCheckDisclosure.For(instanceId));
        var fields = section.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!);
        fields.Keys.ShouldBe(["UpdateCheckCron", "UpdateCheckEmail", "InstanceId"], ignoreOrder: true);
        fields["UpdateCheckEmail"].GetProperty("currentValue").GetString().ShouldBe("security");
    }

    [Test]
    public async Task ConfigView_ShowsTheInstanceId_AsInformation_NotAsASetting()
    {
        Start();

        var section = await UpdateCheckSection();

        var instanceId = (await Json(await Send(HttpMethod.Get, InfoUrl))).GetProperty("instanceId").GetString()!;
        var field = section.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "InstanceId");
        field.GetProperty("fieldType").GetString().ShouldBe(ConfigurationFields.InfoFieldType);
        field.GetProperty("currentValue").GetString().ShouldBe(instanceId);
        section.GetProperty("isActive").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task ConfigView_SaysTheCheckIsDisabled_WhenItIs()
    {
        Start(new Dictionary<string, string> { ["AppSettings:UpdateCheckCron"] = "disabled" });

        var section = await UpdateCheckSection();

        section.GetProperty("description").GetString().ShouldStartWith("Update check: disabled");
        section.GetProperty("description").GetString()!.ShouldContain(UpdateCheckDisclosure.DocsUrl);
        section.GetProperty("isActive").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ConfigView_ExplainsWhyNoUpdateEmailArrives_WhileEmailIsOff()
    {
        Start();

        var section = await UpdateCheckSection();

        var email = section.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "UpdateCheckEmail");
        email.GetProperty("description").GetString()!.ShouldContain("E-mail is off");
    }

    [Test]
    public async Task ConfigView_HasNoEmailNote_WhenEmailIsOn()
    {
        Start(new Dictionary<string, string>
        {
            ["AppSettings:Smtp:Enabled"] = "true",
            ["AppSettings:Smtp:Host"] = "smtp.example.org",
            ["AppSettings:Smtp:FromAddress"] = "registry@example.org"
        });

        var section = await UpdateCheckSection();

        var email = section.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "UpdateCheckEmail");
        email.GetProperty("description").GetString()!.ShouldNotContain("E-mail is off");
    }

    [Test]
    public void TheHost_RunsTheInstanceIdentityTask_BeforeTheRecurringJobs()
    {
        Start();

        var tasks = _factory.Services.CreateScope().ServiceProvider.GetServices<Registry.Web.Hosting.IStartupTask>()
            .OrderBy(t => t.Order).Select(t => t.GetType().Name).ToList();

        tasks.IndexOf("InstanceIdInitializer").ShouldBeGreaterThan(tasks.IndexOf("CacheInitializer"));
        tasks.IndexOf("InstanceIdInitializer").ShouldBeLessThan(tasks.IndexOf("HangfireJobsInitializer"));
    }
}
