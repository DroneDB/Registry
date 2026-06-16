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
            {
                conn.Bind(new NetworkCredential(_settings.BindDn, _settings.BindPassword));

                return Task.FromResult(
                    HealthCheckResult.Healthy(
                        $"LDAP server {_settings.Server}:{_settings.Port} is reachable and the service bind succeeded"));
            }

            // No service account configured: attempt an anonymous bind. Many LDAP/AD servers reject
            // anonymous binds by policy even though they are perfectly reachable, so a rejected bind
            // is reported as Degraded (reachable but not validated) rather than Unhealthy.
            try
            {
                conn.Bind(); // anonymous bind - tests TCP connectivity
            }
            catch (LdapException ex) when (!IsConnectivityError(ex.ErrorCode))
            {
                return Task.FromResult(
                    HealthCheckResult.Degraded(
                        $"LDAP server {_settings.Server}:{_settings.Port} is reachable but the anonymous bind was " +
                        $"rejected ({ex.Message}). Configure BindDn to fully validate connectivity."));
            }

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

    /// <summary>
    /// Returns true for LDAP error codes that indicate the server could not be reached
    /// (as opposed to the server being reachable but rejecting the bind).
    /// </summary>
    private static bool IsConnectivityError(int errorCode) =>
        errorCode is 81  // LDAP_SERVER_DOWN
                  or 85  // LDAP_TIMEOUT
                  or 91; // LDAP_CONNECT_ERROR
}
