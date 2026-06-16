using System;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.HealthChecks;

/// <summary>
/// Verifies connectivity to the configured LDAP server by attempting a bind
/// with the service account (or an anonymous bind if no account is configured).
/// Registered only when <see cref="LdapSettings.Enabled"/> is true.
/// </summary>
public class LdapHealthCheck : IHealthCheck
{
    private readonly LdapSettings _settings;

    public LdapHealthCheck(IOptions<AppSettings> appSettings)
    {
        _settings = appSettings.Value.LdapSettings;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_settings?.Enabled != true)
            return Task.FromResult(HealthCheckResult.Healthy("LDAP authentication is disabled"));

        try
        {
            var identifier = new LdapDirectoryIdentifier(
                _settings.Server, _settings.Port, false, false);

            using var conn = new LdapConnection(identifier)
            {
                AuthType = AuthType.Basic,
                Timeout = TimeSpan.FromSeconds(_settings.Timeout)
            };

            conn.SessionOptions.ProtocolVersion = 3;

            if (_settings.UseSsl)
            {
                conn.SessionOptions.SecureSocketLayer = true;
                if (!_settings.ValidateSslCertificate)
                    conn.SessionOptions.VerifyServerCertificate = (_, _) => true;
            }

            if (!string.IsNullOrWhiteSpace(_settings.BindDn))
                conn.Bind(new NetworkCredential(_settings.BindDn, _settings.BindPassword));
            else
                conn.Bind(); // anonymous bind - tests TCP connectivity

            return Task.FromResult(
                HealthCheckResult.Healthy(
                    $"LDAP server {_settings.Server}:{_settings.Port} is reachable"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy(
                    $"LDAP server {_settings.Server}:{_settings.Port} is not reachable: {ex.Message}", ex));
        }
    }
}
