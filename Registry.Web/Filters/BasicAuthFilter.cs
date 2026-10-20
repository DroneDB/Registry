#nullable enable
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using System;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Utilities.Auth;

namespace Registry.Web.Filters;

/// <summary>
/// Authenticates a request that carries HTTP Basic credentials (OGC clients, scripts) as the
/// matching user. It runs as an authorization filter, declared with
/// <see cref="BasicAuthenticationAttribute"/>, so that the user is known before any access check
/// (<c>OgcAuthorizationFilter</c>, the action's own checks). Missing or wrong credentials leave
/// the request anonymous: what an anonymous user may read stays readable.
/// </summary>
public class BasicAuthFilter : IAsyncAuthorizationFilter
{
    private readonly IUsersManager _usersManager;
    private readonly UserManager<User> _userManager;
    private readonly AppSettings _settings;

    public BasicAuthFilter(IUsersManager usersManager, UserManager<User> userManager, IOptions<AppSettings> settings)
    {
        _usersManager = usersManager;
        _userManager = userManager;
        _settings = settings.Value;
    }

    public static void SendBasicAuthRequest(HttpResponse response)
    {
        response.Headers.Append("WWW-Authenticate", "Basic realm=\"DroneDB\"");
        response.StatusCode = 401;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;
        if (!TryReadCredentials(httpContext.Request.Headers.Authorization, out var username, out var password))
            return;

        var res = await _usersManager.Authenticate(username, password);
        if (res == null)
            return;

        var user = await _userManager.FindByNameAsync(res.Username);
        if (user == null)
            return;
        var isAdmin = await _userManager.IsInRoleAsync(user, ApplicationDbContext.AdminRoleName);

        httpContext.Response.Cookies.Append(_settings.AuthCookieName, res.Token,
            AuthCookieOptions.For(httpContext.Request));

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, user.Id),
            new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), isAdmin.ToString())
        ], "authenticated");
        httpContext.User = new ClaimsPrincipal(identity);
    }

    /// <summary>Reads "Basic base64(user:password)"; false for any other or malformed header.</summary>
    public static bool TryReadCredentials(string? header, out string username, out string password)
    {
        username = password = string.Empty;
        if (header == null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.Latin1.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex < 0)
            return false;

        username = decoded[..separatorIndex];
        password = decoded[(separatorIndex + 1)..];
        return true;
    }
}

/// <summary>
/// Accepts HTTP Basic credentials on a controller or action: runs <see cref="BasicAuthFilter"/>
/// before every other filter (lowest order), so access checks see the authenticated user.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class BasicAuthenticationAttribute : Microsoft.AspNetCore.Mvc.ServiceFilterAttribute
{
    public BasicAuthenticationAttribute() : base(typeof(BasicAuthFilter))
    {
        Order = int.MinValue;
    }
}
