#nullable enable
using System;

namespace Registry.Web.Services.Hub;

/// <summary>
/// The addresses of Hub pages that the server hands out (share results, e-mails). One place for the Hub's routes, so a
/// change of a route on the Hub side is one change here.
/// </summary>
public static class HubLinks
{
    /// <summary>
    /// The query that makes the Hub open the Preferences window on its Notifications tab (after the login, when the
    /// visitor is not signed in).
    /// </summary>
    public const string NotificationPreferencesPath = "/r?preferences=notifications";

    /// <summary>The path of the dataset page, relative to the Hub root: <c>/r/{org}/{ds}</c>.</summary>
    public static string DatasetPath(string orgSlug, string dsSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(dsSlug);

        // Slugs are URL-safe already; escaping keeps the path one segment each if a legacy slug is not
        return $"/r/{Uri.EscapeDataString(orgSlug)}/{Uri.EscapeDataString(dsSlug)}";
    }

    /// <summary>The dataset page under <paramref name="baseUrl"/>; null without a public URL.</summary>
    public static Uri? Dataset(Uri? baseUrl, string orgSlug, string dsSlug) => Under(baseUrl, DatasetPath(orgSlug, dsSlug));

    /// <summary>The Notifications tab of the Preferences window under <paramref name="baseUrl"/>; null without a public URL.</summary>
    public static Uri? NotificationPreferences(Uri? baseUrl) => Under(baseUrl, NotificationPreferencesPath);

    /// <summary>
    /// <paramref name="path"/> (a Hub path such as <c>/images/x.png</c>) under <paramref name="baseUrl"/>, which may
    /// itself have a path (a Hub served under <c>https://example.org/hub/</c>); null without a public URL.
    /// </summary>
    public static Uri? Under(Uri? baseUrl, string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (baseUrl == null)
            return null;

        // new Uri(base, "/x") would drop the path of the base: resolve against the base as a folder instead
        var folder = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        return new Uri(folder, path.TrimStart('/'));
    }
}
