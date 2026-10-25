#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Hosting;

/// <summary>
/// The start-up banner of the web host (mode, version, URLs, useful links), logged under
/// <see cref="RegistryHost.StartupLogCategory"/>. Secrets (admin password, monitor token) are never
/// written: logs end up in container logs and files.
/// </summary>
public static class StartupBanner
{
    /// <summary>The lines of the banner.</summary>
    /// <param name="environmentName">The host environment.</param>
    /// <param name="serverAddresses">The addresses the server listens on.</param>
    /// <param name="http">The external URL override.</param>
    /// <param name="identity">The default admin (its user name only).</param>
    /// <param name="smtp">The outgoing e-mail server (its address only), when known.</param>
    public static IReadOnlyList<string> Lines(string? environmentName, ICollection<string>? serverAddresses,
        HttpHostOptions http, IdentityProviderOptions identity, SmtpSettings? smtp = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(identity);

        var lines = new List<string>
        {
            $"Application started in {environmentName ?? "unknown"} mode",
            $"Version: {Assembly.GetExecutingAssembly().GetName().Version}"
        };

        lines.AddRange((serverAddresses ?? []).Select(address => $"Registry url: {address.Replace("0.0.0.0", "localhost")}"));

        if (identity.DefaultAdmin?.UserName is { } admin)
            lines.Add($"Default admin username: {admin}");

        if (smtp != null)
            lines.Add(smtp.Enabled ? $"E-mail: {smtp.Host}:{smtp.Port} ({smtp.Security})" : "E-mail: disabled");

        var url = serverAddresses?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(http.ExternalUrlOverride))
        {
            lines.Add($"External URL: {http.ExternalUrlOverride}");
            url = http.ExternalUrlOverride;
        }

        if (url != null)
        {
            var builder = new UriBuilder(url);
            if (builder.Host == "0.0.0.0")
                builder.Host = "localhost";
            var baseUri = builder.Uri;

            // Health links need the MonitorToken (?token=...) or an admin login: the token is not written
            const string reqAuth = "(req auth)";
            lines.Add($"API Docs (Scalar): {new Uri(baseUri, MagicStrings.ScalarUrl)}");
            lines.Add($"Version: {new Uri(baseUri, MagicStrings.VersionUrl)}");
            lines.Add($"{reqAuth} Quick Health: {new Uri(baseUri, MagicStrings.QuickHealthUrl)}");
            lines.Add($"{reqAuth} Health: {new Uri(baseUri, MagicStrings.HealthUrl)}");
            lines.Add($"{reqAuth} Hangfire: {new Uri(baseUri, MagicStrings.HangFireUrl)}");
        }

        lines.Add("Press Ctrl+C to quit");
        return lines;
    }

    /// <summary>
    /// The warning to log when e-mail is enabled without a public URL: the e-mails then have no link to the
    /// instance and no logo (a mail client cannot resolve a relative address). Null when there is nothing to warn.
    /// </summary>
    public static string? EmailLinksWarning(SmtpSettings smtp, HttpHostOptions http)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(http);

        return smtp.Enabled && string.IsNullOrWhiteSpace(http.ExternalUrlOverride)
            ? "E-mail is enabled but AppSettings:ExternalUrlOverride is not set: e-mails will have no link to this instance and no logo"
            : null;
    }
}
