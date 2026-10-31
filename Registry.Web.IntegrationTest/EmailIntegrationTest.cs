using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Exceptions;
using Registry.Web.HealthChecks;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Email;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Email;
using Registry.Web.Services.Email.Templates;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Registry.Web.Test;

/// <summary>
/// E-mail against a real SMTP server: Mailpit in a throw-away Docker container, which requires STARTTLS (with a
/// self-signed certificate for <c>localhost</c>) and a login, and shows what it received through its HTTP API.
/// Covers the MailKit sender, the failure kinds, the SMTP health check, the admin test endpoint, a queued
/// e-mail read back from the job storage and run, and the digest of the build notifications from end to end.
/// Explicit: it needs a Docker daemon and pulls the <c>axllent/mailpit</c> image.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Network)]
[Explicit("Requires Docker: starts a Mailpit SMTP server in a container.")]
public class EmailIntegrationTest : IDisposable
{
    private const string MailpitImage = "axllent/mailpit:v1.31.6";
    private const ushort SmtpPort = 1025;
    private const ushort HttpPort = 8025;
    private const string UserName = "registry";
    private const string Password = "Smtp-P@ss1";

    private IContainer _container = null!;
    private HttpClient _api = null!;
    private ushort _smtpPort;

    [OneTimeSetUp]
    public async Task StartMailServer()
    {
        _container = new ContainerBuilder(MailpitImage)
            .WithEnvironment("MP_SMTP_AUTH", $"{UserName}:{Password}")
            .WithEnvironment("MP_SMTP_REQUIRE_STARTTLS", "true")
            .WithEnvironment("MP_SMTP_TLS_CERT", "sans:localhost")
            .WithEnvironment("MP_SMTP_TLS_KEY", "sans:localhost")
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(HttpPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPort).ForPath("/readyz")))
            .Build();

        await _container.StartAsync();

        _smtpPort = _container.GetMappedPublicPort(SmtpPort);
        _api = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpPort)}/") };
    }

    [OneTimeTearDown]
    public async Task StopMailServer()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public void Dispose()
    {
        _api?.Dispose();
        GC.SuppressFinalize(this);
    }

    [SetUp]
    public async Task ClearMailbox() => (await _api.DeleteAsync("api/v1/messages")).EnsureSuccessStatusCode();

    /// <summary>The settings of the Mailpit server: STARTTLS on localhost, the self-signed certificate accepted.</summary>
    private SmtpSettings Settings(Action<SmtpSettings>? tweak = null)
    {
        var settings = new SmtpSettings
        {
            Enabled = true,
            Host = "localhost",
            Port = _smtpPort,
            Security = SmtpSecurity.StartTls,
            ValidateServerCertificate = false,
            UserName = UserName,
            Password = Password,
            FromAddress = "registry@example.org",
            FromName = "Registry Tests",
            ReplyTo = "support@example.org",
            TimeoutSeconds = 10
        };
        tweak?.Invoke(settings);
        return settings;
    }

    private Dictionary<string, string> HostSettings() => new()
    {
        ["AppSettings:Smtp:Enabled"] = "true",
        ["AppSettings:Smtp:Host"] = "localhost",
        ["AppSettings:Smtp:Port"] = _smtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["AppSettings:Smtp:ValidateServerCertificate"] = "false",
        ["AppSettings:Smtp:UserName"] = UserName,
        ["AppSettings:Smtp:Password"] = Password,
        ["AppSettings:Smtp:FromAddress"] = "registry@example.org",
        ["AppSettings:ExternalUrlOverride"] = "https://hub.example.org"
    };

    private static MailKitEmailSender Sender(SmtpSettings settings) =>
        new(MsOptions.Create(settings), MsOptions.Create(new HubOptions { AppName = "My Hub" }),
            NullLogger<MailKitEmailSender>.Instance);

    /// <summary>The one message Mailpit holds, with its parts and headers.</summary>
    private async Task<JsonElement> ReceivedMessage()
    {
        var list = await _api.GetFromJsonAsync<JsonElement>("api/v1/messages");
        var id = list.GetProperty("messages").EnumerateArray().ShouldHaveSingleItem().GetProperty("ID").GetString();
        return await _api.GetFromJsonAsync<JsonElement>($"api/v1/message/{id}");
    }

    private static IEnumerable<string?> Addresses(JsonElement message, string list) =>
        message.GetProperty(list).EnumerateArray().Select(a => a.GetProperty("Address").GetString());

    [Test]
    public async Task Send_DeliversBothPartsAndTheHeaders()
    {
        var message = new EmailMessage([new EmailAddress("jane@example.com", "Jane")], "Integration", "Plain body",
            "<p>HTML body</p>") { Cc = [new EmailAddress("cc@example.com")], Bcc = [new EmailAddress("bcc@example.com")] };

        await Sender(Settings()).SendAsync(message, CancellationToken.None);

        var received = await ReceivedMessage();
        received.GetProperty("Subject").GetString().ShouldBe("Integration");
        received.GetProperty("Text").GetString()!.Trim().ShouldBe("Plain body");
        received.GetProperty("HTML").GetString().ShouldBe("<p>HTML body</p>");
        received.GetProperty("From").GetProperty("Address").GetString().ShouldBe("registry@example.org");
        received.GetProperty("From").GetProperty("Name").GetString().ShouldBe("Registry Tests");
        Addresses(received, "To").ShouldBe(["jane@example.com"]);
        Addresses(received, "Cc").ShouldBe(["cc@example.com"]);
        Addresses(received, "ReplyTo").ShouldBe(["support@example.org"]);
        // The Bcc recipient gets the message but no header names it
        Addresses(received, "Bcc").ShouldBe(["bcc@example.com"]);
    }

    [Test]
    public async Task WrongPassword_IsAPermanentFailure()
    {
        var ex = await Should.ThrowAsync<EmailDeliveryException>(() =>
            Sender(Settings(s => s.Password = "wrong")).SendAsync(new EmailMessage([new EmailAddress("j@example.com")], "x", "y"),
                CancellationToken.None));

        ex.IsTransient.ShouldBeFalse();
        ex.Message.ShouldContain("refused the login");
    }

    [Test]
    public async Task SelfSignedCertificate_IsRefused_WhileValidationIsOn()
    {
        var ex = await Should.ThrowAsync<EmailDeliveryException>(() =>
            Sender(Settings(s => s.ValidateServerCertificate = true))
                .SendAsync(new EmailMessage([new EmailAddress("j@example.com")], "x", "y"), CancellationToken.None));

        ex.IsTransient.ShouldBeFalse();
        ex.Message.ShouldContain("TLS handshake");
    }

    [Test]
    public async Task CertificateForAnotherHost_IsRefused_EvenWithoutValidation()
    {
        // The certificate names localhost: 127.0.0.1 does not match it
        var ex = await Should.ThrowAsync<EmailDeliveryException>(() =>
            Sender(Settings(s => s.Host = "127.0.0.1"))
                .SendAsync(new EmailMessage([new EmailAddress("j@example.com")], "x", "y"), CancellationToken.None));

        ex.Message.ShouldContain("TLS handshake");
    }

    [Test]
    public async Task HealthCheck_IsHealthy_AgainstTheServer_AndDegraded_WithAWrongLogin()
    {
        using var healthy = new SmtpHealthCheck(MsOptions.Create(Settings()), TimeProvider.System);
        var result = await healthy.CheckHealthAsync(new HealthCheckContext());
        result.Status.ShouldBe(HealthStatus.Healthy, result.Description);
        result.Description.ShouldNotBeNull().ShouldContain("login succeeded");

        using var degraded = new SmtpHealthCheck(MsOptions.Create(Settings(s => s.Password = "wrong")), TimeProvider.System);
        (await degraded.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Degraded);
    }

    [Test]
    public async Task TestEndpoint_SendsTheTestEmail()
    {
        using var factory = new RegistryWebFactory(HostSettings());
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/sys/email/test")
        {
            Content = JsonContent.Create(new { to = "admin@example.com" })
        };
        request.AsUser(RegistryWebFactory.AdminUserName, true);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        var received = await ReceivedMessage();
        received.GetProperty("Subject").GetString().ShouldBe("DroneDB test e-mail");
        received.GetProperty("HTML").GetString().ShouldNotBeNull().ShouldContain("This is a test e-mail from DroneDB.");
        received.GetProperty("Text").GetString().ShouldNotBeNull().ShouldContain("https://hub.example.org/");
    }

    [Test]
    public async Task QueuedEmail_IsStored_ThenSentByItsJob()
    {
        using var factory = new RegistryWebFactory(HostSettings());
        using var scope = factory.Services.CreateScope();

        var jobId = await scope.ServiceProvider.GetRequiredService<IEmailService>().QueueAsync(TestEmailTemplate.Instance,
            new TestEmailModel("tester", DateTimeOffset.UtcNow), [new EmailAddress("queued@example.com")]);

        // The factory runs Hangfire without servers: read the job back from the storage and run it, as a server would
        using var connection = scope.ServiceProvider.GetRequiredService<JobStorage>().GetConnection();
        var job = connection.GetJobData(jobId.ShouldNotBeNull()).Job;
        job.Type.ShouldBe(typeof(SendEmailJob));
        await (Task)job.Method.Invoke(scope.ServiceProvider.GetRequiredService<SendEmailJob>(),
            [job.Args[0], CancellationToken.None])!;

        Addresses(await ReceivedMessage(), "To").ShouldBe(["queued@example.com"]);
    }

    [Test]
    public async Task UpdateEmail_GoesToTheAdmins_InOneMessage_AndTheVersionIsRecorded()
    {
        using var factory = new RegistryWebFactory(HostSettings());
        await factory.CreateUserAsync("ops", isAdmin: true);
        await factory.CreateUserAsync("plain");
        using var scope = factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IUpdateNotifier>().NotifyAsync(new UpdateInfoDto
        {
            CurrentVersion = "3.0.0",
            LatestVersion = "3.1.0",
            IsUpdateAvailable = true,
            IsSecurityUpdate = true,
            ReleaseNotes = "## Security\n- Fixes a flaw (v3.1.0)\n- <script>alert(1)</script>\n\nAll changes: https://github.com/DroneDB/Registry/compare/v3.0.0...v3.1.0",
            CompareUrl = "https://github.com/DroneDB/Registry/compare/v3.0.0...v3.1.0"
        }, CancellationToken.None);

        var received = await ReceivedMessage();
        received.GetProperty("Subject").GetString().ShouldBe("Registry 3.1.0: security update");
        Addresses(received, "To").OrderBy(a => a).ShouldBe([RegistryWebFactory.AdminEmail, "ops@example.com"]);
        received.GetProperty("Text").GetString().ShouldNotBeNull().ShouldSatisfyAllConditions(
            t => t.ShouldContain("Latest version: 3.1.0"),
            t => t.ShouldContain("## Security"),
            t => t.ShouldContain("AppSettings:UpdateCheckEmail"),
            t => t.ShouldContain("https://hub.example.org/"));
        var html = received.GetProperty("HTML").GetString().ShouldNotBeNull();
        html.ShouldContain("white-space:pre-wrap");
        html.ShouldNotContain("<script>");
        scope.ServiceProvider.GetRequiredService<IUpdateCheckCache>().Read().LastNotifiedVersion.ShouldBe("3.1.0");
    }

    [Test]
    public async Task BuildNotifications_OneEmailListsTheBuildsOfTheWindow_WithTheOptOutLink()
    {
        var settings = HostSettings();
        settings["AppSettings:BuildNotifications:DigestMinutes"] = "1";
        using var factory = new RegistryWebFactory(settings);
        var jane = await factory.CreateUserAsync("jane");
        using var client = factory.CreateClient();
        var patch = new HttpRequestMessage(HttpMethod.Patch, "/users/preferences")
        {
            Content = JsonContent.Create(new { notifications = new { builds = "all" } })
        };
        patch.AsUser("jane");
        (await client.SendAsync(patch)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Two builds of jane end in the window, as the state filter reports them
        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IBuildNotificationRecorder>();
        await recorder.RecordAsync(new BuildReport("public", "default", "cloud.laz", "cloud-hash", BuildOutcome.Completed,
            null, new BuildRequest(jane.Id, "build-1")), CancellationToken.None);
        await recorder.RecordAsync(new BuildReport("public", "default", "broken.tif", "broken-hash", BuildOutcome.Failed,
            "DdbException: cannot open <broken.tif>", new BuildRequest(jane.Id, "build-2")), CancellationToken.None);

        // The factory runs Hangfire without servers: run the one digest scheduled, then the e-mail it queued
        var storage = scope.ServiceProvider.GetRequiredService<JobStorage>();
        var digest = storage.GetMonitoringApi().ScheduledJobs(0, 10).Select(j => j.Value.Job)
            .Where(j => j.Type == typeof(BuildNotificationDigestJob)).ShouldHaveSingleItem();
        await (Task)digest.Method.Invoke(scope.ServiceProvider.GetRequiredService<BuildNotificationDigestJob>(),
            [digest.Args[0], CancellationToken.None])!;
        var send = storage.GetMonitoringApi().EnqueuedJobs("default", 0, 10).Select(j => j.Value.Job)
            .Where(j => j.Type == typeof(SendEmailJob)).ShouldHaveSingleItem();
        await (Task)send.Method.Invoke(scope.ServiceProvider.GetRequiredService<SendEmailJob>(),
            [send.Args[0], CancellationToken.None])!;

        var received = await ReceivedMessage();
        Addresses(received, "To").ShouldBe(["jane@example.com"]);
        received.GetProperty("Subject").GetString().ShouldNotBeNull().ShouldContain("1 build failed, 1 completed");
        received.GetProperty("Text").GetString().ShouldNotBeNull().ShouldSatisfyAllConditions(
            t => t.ShouldContain("cloud.laz"),
            t => t.ShouldContain("broken.tif"),
            t => t.ShouldContain("https://hub.example.org/r?preferences=notifications"));
        var html = received.GetProperty("HTML").GetString().ShouldNotBeNull();
        html.ShouldContain("href=\"https://hub.example.org/r?preferences=notifications\"");
        html.ShouldNotContain("<broken.tif>");
    }
}
