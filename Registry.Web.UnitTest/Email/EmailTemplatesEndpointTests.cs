#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// The editable e-mails of the web host (<c>/sys/email/templates</c>) and <c>GET /users/notifications/status</c>, through
/// the whole pipeline, with a recording transport: no mail server.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~EmailTemplatesEndpointTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public class EmailTemplatesEndpointTests : IDisposable
{
    private const string Url = "/sys/email/templates";
    private const string Name = BuildNotificationEmailTemplate.TemplateName;
    private const string RegularUser = "templates-regular-user";

    private static readonly Dictionary<string, string> Enabled = new()
    {
        ["AppSettings:Smtp:Enabled"] = "true",
        ["AppSettings:Smtp:Host"] = "smtp.example.org",
        ["AppSettings:Smtp:FromAddress"] = "registry@example.org",
        ["AppSettings:ExternalUrlOverride"] = "https://hub.example.org"
    };

    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private RegistryWebFactory _factory = null!;
    private HttpClient? _client;
    private RecordingSender _sender = null!;

    private void Start(Dictionary<string, string>? settings)
    {
        _sender = new RecordingSender();
        _factory = new RegistryWebFactory(settings,
            services => services.Replace(ServiceDescriptor.Singleton<IEmailSender>(_sender)));
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

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Send(HttpMethod method, string path, object? body = null,
        string user = RegistryWebFactory.AdminUserName, bool isAdmin = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null)
            request.Content = JsonContent.Create(body);
        request.AsUser(user, isAdmin);

        using var response = await _client!.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private static string Detail(JsonNode? body) => body!["detail"]!.GetValue<string>();

    [Test]
    public async Task List_IsAnArray_WithTheBuildNotificationTemplate()
    {
        Start(Enabled);

        var (status, body) = await Send(HttpMethod.Get, Url);

        status.ShouldBe(HttpStatusCode.OK);
        var item = body.ShouldBeOfType<JsonArray>().ShouldHaveSingleItem()!;
        item["name"]!.GetValue<string>().ShouldBe(Name);
        item["title"]!.GetValue<string>().ShouldBe("Build notifications");
        item["isDefault"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task Get_IsTheBuiltInText_WithEveryPlaceholder()
    {
        Start(Enabled);

        var (status, body) = await Send(HttpMethod.Get, $"{Url}/{Name}");

        status.ShouldBe(HttpStatusCode.OK);
        body!["isDefault"]!.GetValue<bool>().ShouldBeTrue();
        body["subject"]!.GetValue<string>().ShouldBe(body["defaultSubject"]!.GetValue<string>());
        body["body"]!.GetValue<string>().ShouldBe(body["defaultBody"]!.GetValue<string>());
        var placeholders = body["placeholders"].ShouldBeOfType<JsonArray>()
            .ToDictionary(p => p!["name"]!.GetValue<string>(), p => p!["inSubject"]!.GetValue<bool>());
        placeholders.ShouldBe(new Dictionary<string, bool>
        {
            ["appName"] = true, ["user"] = true, ["summary"] = true, ["failedCount"] = true, ["completedCount"] = true,
            ["datasets"] = true, ["builds"] = false, ["link"] = false, ["preferencesLink"] = false
        }, ignoreOrder: true);
        body["placeholders"]!.AsArray().ShouldAllBe(p => p!["description"]!.GetValue<string>().Length > 0);
    }

    [Test]
    public async Task AnUnknownName_Is404()
    {
        Start(Enabled);

        (await Send(HttpMethod.Get, $"{Url}/no-such-template")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Put, $"{Url}/no-such-template", new { subject = "s", body = "b" })).Status.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"{Url}/no-such-template")).Status.ShouldBe(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Post, $"{Url}/no-such-template/preview", new { subject = "s", body = "b" })).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Put_SavesTheText_ForEveryNode_AndDeleteGoesBackToTheBuiltInText()
    {
        Start(Enabled);

        var (status, saved) = await Send(HttpMethod.Put, $"{Url}/{Name}",
            new { subject = "Builds on {{appName}}", body = "Hi {{user}}\n\n{{builds}}" });

        status.ShouldBe(HttpStatusCode.OK);
        saved!["isDefault"]!.GetValue<bool>().ShouldBeFalse();
        saved["subject"]!.GetValue<string>().ShouldBe("Builds on {{appName}}");
        saved["defaultSubject"]!.GetValue<string>().ShouldBe(BuildNotificationEmailTemplate.Instance.BuiltInText.Subject);
        File.Exists(Path.Combine(_factory.StoragePath, MagicStrings.EmailTemplatesFileName)).ShouldBeTrue();

        var (_, read) = await Send(HttpMethod.Get, $"{Url}/{Name}");
        read!["body"]!.GetValue<string>().ShouldBe("Hi {{user}}\n\n{{builds}}");
        (await Send(HttpMethod.Get, Url)).Body![0]!["isDefault"]!.GetValue<bool>().ShouldBeFalse();

        var (resetStatus, reset) = await Send(HttpMethod.Delete, $"{Url}/{Name}");
        resetStatus.ShouldBe(HttpStatusCode.OK);
        reset!["isDefault"]!.GetValue<bool>().ShouldBeTrue();
        reset["subject"]!.GetValue<string>().ShouldBe(BuildNotificationEmailTemplate.Instance.BuiltInText.Subject);
    }

    [TestCase("{{foo}}", "body", "Unknown placeholder {{foo}} in the body")]
    [TestCase("Hello {{user", "body", "Unclosed placeholder")]
    [TestCase("{{builds}}", "subject", "{{builds}} can only be used in the body")]
    [TestCase("{{link}}", "subject", "{{link}} can only be used in the body")]
    [TestCase("  ", "subject", "The subject is empty")]
    [TestCase("", "body", "The body is empty")]
    public async Task Put_AnInvalidText_Is400_NamingTheProblem_AndSavesNothing(string text, string field, string expected)
    {
        Start(Enabled);
        var body = field == "subject" ? new { subject = text, body = "Hi" } : new { subject = "Hi", body = text };

        var (status, problem) = await Send(HttpMethod.Put, $"{Url}/{Name}", body);

        status.ShouldBe(HttpStatusCode.BadRequest);
        Detail(problem).ShouldContain(expected);
        (await Send(HttpMethod.Get, $"{Url}/{Name}")).Body!["isDefault"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task Put_TooLongFields_Are400()
    {
        Start(Enabled);

        Detail((await Send(HttpMethod.Put, $"{Url}/{Name}", new { subject = new string('s', 201), body = "b" })).Body)
            .ShouldContain("200 characters");
        Detail((await Send(HttpMethod.Put, $"{Url}/{Name}", new { subject = "s", body = new string('b', 16 * 1024 + 1) })).Body)
            .ShouldContain("16384 characters");
    }

    [Test]
    public async Task EveryAction_IsForAdministratorsOnly()
    {
        Start(Enabled);
        await _factory.CreateUserAsync(RegularUser);
        var text = new { subject = "s", body = "b" };

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, Url, null), (HttpMethod.Get, $"{Url}/{Name}", null), (HttpMethod.Put, $"{Url}/{Name}", text),
                     (HttpMethod.Delete, $"{Url}/{Name}", null), (HttpMethod.Post, $"{Url}/{Name}/preview", text),
                     (HttpMethod.Post, $"{Url}/{Name}/test", text)
                 })
            (await Send(method, path, body, RegularUser, isAdmin: false)).Status.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");

        _sender.Sent.ShouldBeEmpty();
    }

    [Test]
    public async Task Preview_RendersTheUnsavedText_WithTheSample_InTheBrandedLayout()
    {
        Start(Enabled);

        var (status, preview) = await Send(HttpMethod.Post, $"{Url}/{Name}/preview",
            new { subject = "{{summary}} <b>now</b>", body = "Hello {{user}} <script>x</script>\n\n{{builds}}\n\n{{link}}" });

        status.ShouldBe(HttpStatusCode.OK);
        preview!["subject"]!.GetValue<string>().ShouldBe("1 build failed, 1 completed <b>now</b>");
        preview["text"]!.GetValue<string>().ShouldContain($"Hello {RegistryWebFactory.AdminUserName} <script>x</script>");
        var html = preview["html"]!.GetValue<string>();
        html.ShouldStartWith("<!DOCTYPE html>");
        html.ShouldContain("&lt;script&gt;x&lt;/script&gt;");
        html.ShouldNotContain("<script>");
        html.ShouldContain("cloud.laz");
        html.ShouldContain("href=\"https://hub.example.org/r/demo/survey-2026\"");
        // Not saved
        (await Send(HttpMethod.Get, $"{Url}/{Name}")).Body!["isDefault"]!.GetValue<bool>().ShouldBeTrue();
        _sender.Sent.ShouldBeEmpty();
    }

    [Test]
    public async Task Preview_AnInvalidText_Is400()
    {
        Start(Enabled);

        var (status, problem) = await Send(HttpMethod.Post, $"{Url}/{Name}/preview", new { subject = "{{nope}}", body = "b" });

        status.ShouldBe(HttpStatusCode.BadRequest);
        Detail(problem).ShouldContain("Unknown placeholder {{nope}} in the subject");
    }

    [Test]
    public async Task Test_SendsTheSampleToTheCallingAdministrator()
    {
        Start(Enabled);

        var (status, _) = await Send(HttpMethod.Post, $"{Url}/{Name}/test", new { subject = "Test {{appName}}", body = "Hi {{user}}" });

        status.ShouldBe(HttpStatusCode.NoContent);
        var message = _sender.Sent.ShouldHaveSingleItem();
        message.To.ShouldHaveSingleItem().Address.ShouldBe(RegistryWebFactory.AdminEmail);
        message.Subject.ShouldBe("Test DroneDB");
        message.TextBody.ShouldStartWith($"Hi {RegistryWebFactory.AdminUserName}");
    }

    [Test]
    public async Task Test_WithEmailOff_Is400_AndSendsNothing()
    {
        Start(null);

        var (status, problem) = await Send(HttpMethod.Post, $"{Url}/{Name}/test", new { subject = "s", body = "b" });

        status.ShouldBe(HttpStatusCode.BadRequest);
        Detail(problem).ShouldContain("E-mail is disabled");
        _sender.Sent.ShouldBeEmpty();
    }

    [Test]
    public async Task Test_AnAdministratorWithoutAnAddress_Is400()
    {
        Start(Enabled);
        await ClearEmail(RegistryWebFactory.AdminUserName);

        var (status, problem) = await Send(HttpMethod.Post, $"{Url}/{Name}/test", new { subject = "s", body = "b" });

        status.ShouldBe(HttpStatusCode.BadRequest);
        Detail(problem).ShouldContain("no valid e-mail address");
        _sender.Sent.ShouldBeEmpty();
    }

    // ---- GET /users/notifications/status ----

    [Test]
    public async Task NotificationStatus_CanReceive_WhenEmailIsOnAndTheUserHasAnAddress()
    {
        Start(Enabled);
        await _factory.CreateUserAsync(RegularUser);

        var (status, body) = await Send(HttpMethod.Get, "/users/notifications/status", user: RegularUser, isAdmin: false);

        status.ShouldBe(HttpStatusCode.OK);
        body!.ToJsonString().ShouldBe("""{"canReceiveEmail":true,"reason":null}""");
    }

    [Test]
    public async Task NotificationStatus_EmailOff_IsEmailDisabled()
    {
        Start(null);
        await _factory.CreateUserAsync(RegularUser);

        var (_, body) = await Send(HttpMethod.Get, "/users/notifications/status", user: RegularUser, isAdmin: false);

        body!.ToJsonString().ShouldBe("""{"canReceiveEmail":false,"reason":"emailDisabled"}""");
    }

    [Test]
    public async Task NotificationStatus_NoAddress_IsNoAddress()
    {
        Start(Enabled);
        await _factory.CreateUserAsync(RegularUser);
        await ClearEmail(RegularUser);

        var (_, body) = await Send(HttpMethod.Get, "/users/notifications/status", user: RegularUser, isAdmin: false);

        body!.ToJsonString().ShouldBe("""{"canReceiveEmail":false,"reason":"noAddress"}""");
    }

    [Test]
    public async Task NotificationStatus_Anonymous_Is401()
    {
        Start(Enabled);

        using var response = await _client!.GetAsync("/users/notifications/status");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task ClearEmail(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByNameAsync(userName) ?? throw new InvalidOperationException(userName);
        (await users.SetEmailAsync(user, null)).Succeeded.ShouldBeTrue();
    }
}
