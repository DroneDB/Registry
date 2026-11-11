using Microsoft.AspNetCore.Http;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Options for the auth cookie (<c>AppSettings.AuthCookieName</c>) when the server sets it
/// (<c>BasicAuthFilter</c>) or deletes it (<c>POST /users/logout</c>): one source, so the delete matches
/// the cookie that was set.
/// </summary>
public static class AuthCookieOptions
{
    /// <summary>
    /// Not readable from scripts (<c>HttpOnly</c>), never sent cross-site (<c>SameSite=Strict</c>, as the
    /// cookie the web client sets), site-wide (<c>Path=/</c>) and <c>Secure</c> when the request came
    /// over HTTPS: browsers drop a Secure cookie set over plain HTTP, which would break HTTP setups.
    /// </summary>
    public static CookieOptions For(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/"
    };
}
