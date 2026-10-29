using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NUnit.Framework;
using Registry.Web.HealthChecks;
using Registry.Web.Models.Configuration;
using Shouldly;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Registry.Web.Test.Email;

/// <summary>
/// <see cref="SmtpHealthCheck"/> without a mail server: a failure is Degraded, and a result is reused for
/// <see cref="SmtpHealthCheck.CacheDuration"/>, so polling does not log in to the provider each time. A loopback
/// listener counts the connections the check opens.
/// </summary>
[TestFixture]
public class SmtpHealthCheckTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    public async Task Failure_IsDegraded_AndCached_UntilItExpires()
    {
        // A listener that accepts connections but never speaks SMTP: every probe times out (Degraded)
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var clock = new ManualClock();
            using var check = new SmtpHealthCheck(MsOptions.Create(new SmtpSettings
            {
                Enabled = true, Host = "127.0.0.1", Port = port, Security = SmtpSecurity.None, TimeoutSeconds = 1,
                FromAddress = "registry@example.org"
            }), clock);

            var first = await check.CheckHealthAsync(new HealthCheckContext());
            first.Status.ShouldBe(HealthStatus.Degraded);
            first.Description.ShouldNotBeNull().ShouldContain($"127.0.0.1:{port}");
            using (await listener.AcceptTcpClientAsync()) { }

            // Within the cache window: the same result, no new connection
            clock.Now += SmtpHealthCheck.CacheDuration - TimeSpan.FromSeconds(1);
            (await check.CheckHealthAsync(new HealthCheckContext())).ShouldBe(first);
            listener.Pending().ShouldBeFalse();

            // Expired: a new probe connects again
            clock.Now += TimeSpan.FromSeconds(2);
            var second = await check.CheckHealthAsync(new HealthCheckContext());
            second.Status.ShouldBe(HealthStatus.Degraded);
            second.ShouldNotBe(first);
            listener.Pending().ShouldBeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }
}
