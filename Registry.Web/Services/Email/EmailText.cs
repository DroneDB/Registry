#nullable enable
using System;

namespace Registry.Web.Services.Email;

/// <summary>The plain-text frame of every e-mail: the body of the template, then the signature.</summary>
public static class EmailText
{
    /// <summary>
    /// <paramref name="body"/> followed by the standard signature separator (<c>"-- "</c>, which mail
    /// clients recognize), the application name and the public URL.
    /// </summary>
    public static string Wrap(string body, EmailBranding branding)
    {
        ArgumentNullException.ThrowIfNull(branding);

        var signature = branding.BaseUrl == null
            ? branding.AppName
            : branding.AppName + "\n" + branding.BaseUrl;

        return (body ?? string.Empty).TrimEnd() + "\n\n-- \n" + signature + "\n";
    }
}
