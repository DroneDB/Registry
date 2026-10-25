#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Email;

namespace Registry.Web.HealthChecks;

/// <summary>
/// Opens a session with the SMTP server (connect, TLS, login) and closes it without sending. Registered only while
/// e-mail is enabled, and registered as a singleton because it caches its result.
/// <list type="bullet">
/// <item>A failure is <see cref="HealthStatus.Degraded"/>, not Unhealthy. The Registry works without e-mail (queued
/// e-mails wait for the server), so a mail outage must not make an orchestrator restart or remove it.</item>
/// <item>The result is kept for <see cref="CacheDuration"/>. A monitor that polls every few seconds would otherwise
/// log in to the mail provider each time, and providers rate-limit or block accounts that do.</item>
/// </list>
/// </summary>
public sealed class SmtpHealthCheck : IHealthCheck, IDisposable
{
    /// <summary>How long a probe result is reused.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IOptions<SmtpSettings> _smtp;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _probe = new(1, 1);
    private (HealthCheckResult Result, DateTimeOffset At)? _last;

    public SmtpHealthCheck(IOptions<SmtpSettings> smtp, TimeProvider time)
    {
        _smtp = smtp ?? throw new ArgumentNullException(nameof(smtp));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // One probe at a time: concurrent checks wait for it and share its result
        await _probe.WaitAsync(cancellationToken);
        try
        {
            if (_last is { } last && _time.GetUtcNow() - last.At < CacheDuration)
                return last.Result;

            var result = await ProbeAsync(cancellationToken);
            _last = (result, _time.GetUtcNow());
            return result;
        }
        finally
        {
            _probe.Release();
        }
    }

    public void Dispose() => _probe.Dispose();

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        var settings = _smtp.Value;
        var server = $"{settings.Host}:{settings.Port}";

        try
        {
            await SmtpConnection.RunAsync(settings, (client, token) => client.NoOpAsync(token), ct);
        }
        catch (EmailDeliveryException ex)
        {
            return HealthCheckResult.Degraded(ex.Message, ex);
        }

        return HealthCheckResult.Healthy(string.IsNullOrEmpty(settings.UserName)
            ? $"The mail server {server} is reachable"
            : $"The mail server {server} is reachable and the login succeeded");
    }
}
