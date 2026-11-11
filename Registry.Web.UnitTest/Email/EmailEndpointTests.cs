using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.HealthChecks;
using Registry.Web.Models.Email;
using Registry.Web.Services.Email;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Email;

/// <summary>
/// The e-mail features of the web host: <c>POST /sys/email/test</c>, the SMTP section of the admin configuration
/// view, and what the host registers with e-mail on and off. The transport is a recording fake: no server.
/// </summary>
[TestFixture]
[NonParallelizable]
public class EmailEndpointTests : IDisposable
{
    private const string TestUrl = "/sys/email/test";
    private const string RegularUser = "email-regular-user";

    private static readonly Dictionary<string, string> Enabled = new()
    {
        ["AppSettings:Smtp:Enabled"] = "true",
        ["AppSettings:Smtp:Host"] = "smtp.example.org",
        ["AppSettings:Smtp:UserName"] = "registry",
        ["AppSettings:Smtp:Password"] = "smtp-secret-value",
        ["AppSettings:Smtp:FromAddress"] = "registry@example.org",
        ["AppSettings:ExternalUrlOverride"] = "https://hub.example.org"
    };

    /// <summary>Records what it is asked to send, or fails with <see cref="Failure"/>.</summary>
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public EmailDeliveryException? Failure { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (Failure != null)
                throw Failure;
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

    private Task<HttpResponseMessage> PostTest(string to, string user = RegistryWebFactory.AdminUserName, bool isAdmin = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TestUrl) { Content = JsonContent.Create(new { to }) };
        request.AsUser(user, isAdmin);
        return _client!.SendAsync(request);
    }

    private static async Task<string> Detail(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString()!;

    [Test]
    public async Task TestEmail_Enabled_IsSentToTheAddress()
    {
        Start(Enabled);

        using var response = await PostTest("jane@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var message = _sender.Sent.ShouldHaveSingleItem();
        message.To.ShouldHaveSingleItem().Address.ShouldBe("jane@example.com");
        message.Subject.ShouldBe("DroneDB test e-mail");
        message.HtmlBody.ShouldNotBeNull().ShouldContain($"Sent at the request of {RegistryWebFactory.AdminUserName}");
    }

    [Test]
    public async Task TestEmail_Disabled_IsABadRequest()
    {
        Start(null);

        using var response = await PostTest("jane@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Detail(response)).ShouldContain("E-mail is disabled");
        _sender.Sent.ShouldBeEmpty();
    }

    [Test]
    public async Task TestEmail_InvalidAddress_IsABadRequest()
    {
        Start(Enabled);

        using var response = await PostTest("Jane <jane@example.com>");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _sender.Sent.ShouldBeEmpty();
    }

    [Test]
    public async Task TestEmail_ServerRefusal_IsABadGateway_WithTheReason()
    {
        Start(Enabled);
        _sender.Failure = new EmailDeliveryException("The mail server smtp.example.org:587 replied 535: bad login");

        using var response = await PostTest("jane@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await Detail(response)).ShouldContain("replied 535");
    }

    [Test]
    public async Task TestEmail_NonAdmin_IsForbidden()
    {
        Start(Enabled);
        await _factory.CreateUserAsync(RegularUser);

        using var response = await PostTest("jane@example.com", RegularUser, isAdmin: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _sender.Sent.ShouldBeEmpty();
    }

    /// <summary>Clients read whether the server sends e-mail from the anonymous features, before anyone signs in.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task Features_TellWhetherEmailIsEnabled_ToAnonymousClients(bool enabled)
    {
        Start(enabled ? Enabled : null);

        using var response = await _client!.GetAsync("/sys/features");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        JsonDocument.Parse(body).RootElement.GetProperty("emailEnabled").GetBoolean().ShouldBe(enabled);
        // Nothing of the server's settings beyond the flag
        body.ShouldNotContain("smtp.example.org");
    }

    [Test]
    public async Task ConfigView_ShowsTheSmtpSection_WithoutThePassword()
    {
        Start(Enabled);

        var request = new HttpRequestMessage(HttpMethod.Get, "/sys/config");
        request.AsUser(RegistryWebFactory.AdminUserName, true);
        using var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("smtp-secret-value");

        var section = JsonDocument.Parse(body).RootElement.GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "Smtp");
        section.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        var password = section.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "Password");
        password.GetProperty("sensitive").GetBoolean().ShouldBeTrue();
        password.GetProperty("isSet").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public void Host_Enabled_HasTheSmtpHealthCheck()
    {
        Start(Enabled);

        var checks = _factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        checks.ShouldContain(c => c.Name == "SMTP health check");
        _factory.Services.GetRequiredService<SmtpHealthCheck>().ShouldBeSameAs(_factory.Services.GetRequiredService<SmtpHealthCheck>());
    }

    [Test]
    public void Host_Disabled_HasNoSmtpHealthCheck_AndTheDisabledSender()
    {
        // The real registrations, without the recording sender
        _factory = new RegistryWebFactory();

        var checks = _factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        checks.ShouldNotContain(c => c.Name == "SMTP health check");
        _factory.Services.GetRequiredService<IEmailSender>().ShouldBeOfType<DisabledEmailSender>();
    }

    [Test]
    public void Host_Enabled_SendsWithMailKit()
    {
        _factory = new RegistryWebFactory(Enabled);

        _factory.Services.GetRequiredService<IEmailSender>().ShouldBeOfType<MailKitEmailSender>();
    }
}
