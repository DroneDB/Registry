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
    public static IReadOnlyList<string> Lines(string? environmentName, ICollection<string>? serverAddresses,
        HttpHostOptions http, IdentityProviderOptions identity)
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
}
