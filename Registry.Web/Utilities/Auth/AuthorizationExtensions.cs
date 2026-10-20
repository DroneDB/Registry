using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;

namespace Registry.Web.Utilities.Auth;

public static class AuthorizationExtensions
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Allows the request when it carries the configured MonitorToken (bearer header, else the
    /// "token" query parameter) or when the user is an admin (<see cref="Policies.IsAdmin"/>).
    /// </summary>
    public static IEndpointConventionBuilder RequireAdminOrMonitorToken(
        this IEndpointConventionBuilder builder)
    {
        return builder.RequireAuthorization(policy =>
        {
            policy.RequireAssertion(context =>
            {
                if (context.Resource is HttpContext httpContext && HasMonitorToken(httpContext))
                    return true;

                return Policies.IsAdmin(context.User);
            });
        });
    }

    private static bool HasMonitorToken(HttpContext httpContext)
    {
        var appSettings = httpContext.RequestServices.GetRequiredService<IOptions<AppSettings>>();

        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        var token = !string.IsNullOrEmpty(authHeader) && authHeader.StartsWith(BearerPrefix)
            ? authHeader[BearerPrefix.Length..].Trim()
            : httpContext.Request.Query["token"].FirstOrDefault();

        return TokensMatch(token, appSettings.Value.MonitorToken);
    }

    /// <summary>
    /// Constant-time comparison of a presented token with the expected one; a missing or empty token
    /// (presented or configured) never matches.
    /// </summary>
    internal static bool TokensMatch(string presented, string expected) =>
        !string.IsNullOrEmpty(presented) && !string.IsNullOrEmpty(expected) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}
