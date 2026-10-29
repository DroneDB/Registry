using System;

namespace Registry.Web.Services.Email;

/// <summary>
/// The one rule for an address an e-mail links to: absolute http or https. Anything else (<c>javascript:</c>,
/// <c>data:</c>, a relative path a mail client cannot resolve) is never written into an <c>href</c> or <c>src</c>.
/// </summary>
public static class EmailLinks
{
    /// <summary><paramref name="value"/> as an address when it is an absolute http or https URL, otherwise null.</summary>
    public static Uri? WebUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && IsWebUrl(uri) ? uri : null;

    /// <summary>Whether <paramref name="uri"/> is an absolute http or https URL.</summary>
    public static bool IsWebUrl(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
