using System;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;

namespace Registry.Web.Test.Support;

/// <summary>
/// Test-only authentication scheme selected per request through the <see cref="UserHeader"/> header
/// (<c>X-Test-User: userName[;admin=true|false]</c>). No header means anonymous.
/// The user must exist in the identity store (the default admin always does, see
/// <see cref="RegistryWebFactory.CreateUserAsync"/> for others); the issued claims mirror the ones
/// carried by the app's JWT (<see cref="ClaimTypes.Name"/> = user id, <c>admin</c> flag), so the
/// app resolves the caller exactly as it does in production. Requests without the header but with
/// an <c>Authorization</c> header (or the auth cookie, which the app turns into one) are forwarded
/// to the app's JWT bearer scheme, so real tokens keep working in tests.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";

    private const string AdminKey = "admin";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder)
    {
    }

    /// <summary>
    /// Builds the <see cref="UserHeader"/> value for the given identity.
    /// </summary>
    public static string HeaderValue(string userName, bool isAdmin = false) =>
        $"{userName};{AdminKey}={(isAdmin ? "true" : "false")}";

    /// <summary>
    /// Selects the JWT bearer scheme for requests that carry a real token and no test identity.
    /// </summary>
    public static string? ForwardSelector(HttpContext context) =>
        !context.Request.Headers.ContainsKey(UserHeader) &&
        context.Request.Headers.ContainsKey(HeaderNames.Authorization)
            ? JwtBearerDefaults.AuthenticationScheme
            : null;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = Request.Headers[UserHeader].ToString();

        if (string.IsNullOrWhiteSpace(raw))
            return AuthenticateResult.NoResult();

        if (!TryParse(raw, out var userName, out var isAdmin))
            return AuthenticateResult.Fail($"Malformed {UserHeader} header: '{raw}'");

        var userManager = Context.RequestServices.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByNameAsync(userName);

        if (user == null)
            return AuthenticateResult.Fail($"Unknown test user '{userName}'");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, user.Id),
            new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), isAdmin ? "true" : "false",
                ClaimValueTypes.Boolean)
        ], SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static bool TryParse(string raw, out string userName, out bool isAdmin)
    {
        isAdmin = false;
        var parts = raw.Split(';', StringSplitOptions.TrimEntries);
        userName = parts[0];

        if (string.IsNullOrWhiteSpace(userName))
            return false;

        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;

            var kv = parts[i].Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !string.Equals(kv[0], AdminKey, StringComparison.OrdinalIgnoreCase) ||
                !bool.TryParse(kv[1], out isAdmin))
                return false;
        }

        return true;
    }
}
