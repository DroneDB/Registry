#nullable enable
using System;
using System.IO;
using System.Linq;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Services.Email;

/// <summary>
/// What every e-mail shows of this instance: the application name, the public URL and the logo.
/// </summary>
/// <param name="AppName">The Hub application name (<see cref="HubOptions.AppName"/>), "DroneDB" when not set.</param>
/// <param name="BaseUrl">The public URL (<see cref="HttpHostOptions.ExternalUrlOverride"/>): null when not set, and then e-mails carry no links.</param>
/// <param name="LogoUrl">The logo, only an absolute PNG, JPEG or GIF: mail clients block SVG and cannot resolve a relative path.</param>
public sealed record EmailBranding(string AppName, Uri? BaseUrl, Uri? LogoUrl)
{
    /// <summary>The application name the Hub shows when <see cref="HubOptions.AppName"/> is not set.</summary>
    public const string DefaultAppName = "DroneDB";

    private static readonly string[] RasterExtensions = [".png", ".jpg", ".jpeg", ".gif"];

    /// <summary>The application name of <paramref name="hub"/>.</summary>
    public static string AppNameOf(HubOptions? hub) =>
        hub?.AppName is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : DefaultAppName;

    /// <summary>The branding of an instance configured with <paramref name="hub"/> and <paramref name="http"/>.</summary>
    public static EmailBranding From(HubOptions? hub, HttpHostOptions http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var baseUrl = AbsoluteWebUrl(http.ExternalUrlOverride);
        return new EmailBranding(AppNameOf(hub), baseUrl, LogoOf(hub?.AppLogo, baseUrl));
    }

    private static Uri? LogoOf(string? logo, Uri? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(logo))
            return null;

        // An absolute URL as configured, or a path of this instance (e.g. /branding/logo.png) under its public URL
        var url = AbsoluteWebUrl(logo) ??
                  (baseUrl != null && logo.StartsWith('/') ? new Uri(baseUrl, logo) : null);

        return url != null && RasterExtensions.Contains(Path.GetExtension(url.AbsolutePath), StringComparer.OrdinalIgnoreCase)
            ? url
            : null;
    }

    private static Uri? AbsoluteWebUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
}
