using System;
using System.IO;
using System.Linq;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Hub;

namespace Registry.Web.Services.Email;

/// <summary>
/// What every e-mail shows of this instance: the application name, the public URL, the logo or icon and the accent
/// colour, all read from the settings the Hub already uses (<see cref="HubOptions"/>), so the e-mails look like the Hub
/// that sends them.
/// </summary>
/// <param name="AppName">The Hub application name (<see cref="HubOptions.AppName"/>), "DroneDB" when not set.</param>
/// <param name="BaseUrl">The public URL (<see cref="HttpHostOptions.ExternalUrlOverride"/>): null when not set, and then e-mails carry no links.</param>
/// <param name="LogoUrl">The header logo, only an absolute PNG, JPEG or GIF: mail clients block SVG and cannot resolve a relative path.</param>
public sealed record EmailBranding(string AppName, Uri? BaseUrl, Uri? LogoUrl)
{
    /// <summary>The application name the Hub shows when <see cref="HubOptions.AppName"/> is not set.</summary>
    public const string DefaultAppName = "DroneDB";

    /// <summary>
    /// The logo a stock instance ships for e-mails (a PNG rendition of the Hub's <c>images/logo.svg</c>, which mail
    /// clients would block), served by the Hub under the public URL.
    /// </summary>
    public const string BuiltInLogoPath = "/images/email-logo.png";

    /// <summary>The <see cref="HubOptions.AppLogo"/> the default settings ship: a stock instance, not a chosen logo.</summary>
    public const string StockAppLogo = "/images/logo-banner.svg";

    private static readonly string[] RasterExtensions = [".png", ".jpg", ".jpeg", ".gif"];

    private readonly string _accentColor = EmailStyles.BrandBlue;

    /// <summary>
    /// The accent (header bar, links, buttons) as <c>#rrggbb</c>: <see cref="FaviconOptions.ThemeColor"/>, or the brand
    /// blue. Only a hex colour is accepted, because it is written into inline styles.
    /// </summary>
    public string AccentColor
    {
        get => _accentColor;
        init => _accentColor = EmailStyles.NormalizeHex(value) ??
                               throw new ArgumentException($"'{value}' is not a hex colour", nameof(value));
    }

    /// <summary>The text colour on <see cref="AccentColor"/> (a button label): white or near-black, whichever reads best.</summary>
    public string AccentTextColor => EmailStyles.TextOn(AccentColor);

    /// <summary>The colour of link text on the white body: <see cref="AccentColor"/>, darkened when it is too light to read.</summary>
    public string LinkColor => EmailStyles.ReadableOnWhite(AccentColor);

    /// <summary>
    /// A small icon shown beside the application name when there is no raster logo: the apple-touch icon or the 32 px
    /// favicon (<see cref="FaviconOptions"/>), absolute and raster like the logo. Null when there is a logo.
    /// </summary>
    public Uri? IconUrl { get; init; }

    /// <summary>The application name of <paramref name="hub"/>.</summary>
    public static string AppNameOf(HubOptions? hub) =>
        hub?.AppName is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : DefaultAppName;

    /// <summary>The branding of an instance configured with <paramref name="hub"/> and <paramref name="http"/>.</summary>
    public static EmailBranding From(HubOptions? hub, HttpHostOptions http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var baseUrl = EmailLinks.WebUrl(http.ExternalUrlOverride);
        var logo = RasterUrl(hub?.AppLogo, baseUrl);
        Uri? icon = null;

        if (logo == null)
        {
            icon = RasterUrl(hub?.Favicon?.AppleTouchIcon, baseUrl) ?? RasterUrl(hub?.Favicon?.Favicon32, baseUrl);

            // The DroneDB logo only where nothing else is branded: a white-labelled instance never shows it
            if (icon == null && IsStock(hub))
                logo = HubLinks.Under(baseUrl, BuiltInLogoPath);
        }

        return new EmailBranding(AppNameOf(hub), baseUrl, logo)
        {
            AccentColor = EmailStyles.NormalizeHex(hub?.Favicon?.ThemeColor) ?? EmailStyles.BrandBlue,
            IconUrl = icon
        };
    }

    /// <summary>
    /// No application name, no icon class and no logo of its own (none, or the banner the default settings ship): the
    /// Hub then shows the DroneDB banner itself, and so may the e-mails.
    /// </summary>
    private static bool IsStock(HubOptions? hub) =>
        string.IsNullOrWhiteSpace(hub?.AppName) &&
        string.IsNullOrWhiteSpace(hub?.AppIcon) &&
        (string.IsNullOrWhiteSpace(hub?.AppLogo) || string.Equals(hub.AppLogo.Trim(), StockAppLogo, StringComparison.OrdinalIgnoreCase));

    private static Uri? RasterUrl(string? configured, Uri? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        // An absolute URL as configured, or a path of this instance (e.g. /branding/logo.png) under its public URL
        var url = EmailLinks.WebUrl(configured) ??
                  (baseUrl != null && configured.StartsWith('/') ? new Uri(baseUrl, configured) : null);

        return url != null && RasterExtensions.Contains(Path.GetExtension(url.AbsolutePath), StringComparer.OrdinalIgnoreCase)
            ? url
            : null;
    }
}
