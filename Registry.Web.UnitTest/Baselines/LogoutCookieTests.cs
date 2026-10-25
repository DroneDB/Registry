#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The auth cookie <c>BasicAuthFilter</c> sets is <c>HttpOnly</c>, so the web
/// client cannot clear it from JavaScript on sign-out. <c>POST /users/logout</c> (anonymous) expires it
/// server-side with the same name/<c>Path=/</c> it was set with, so the browser actually drops it.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~LogoutCookieTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class LogoutCookieTests
{
    private const string AuthCookieName = "jwtToken";
    private const string LogoutUrl = "/users/logout";

    // An OGC endpoint ([BasicAuthentication]); the dataset need not exist, the filter runs first
    private const string OgcUrl = "/orgs/admin/ds/logoutcookie-missing/wms?service=WMS&request=GetCapabilities";

    private RegistryWebFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _factory = new RegistryWebFactory();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    private HttpClient CreateClient(string baseAddress) =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            HandleCookies = false
        });

    private static string? AuthSetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.SingleOrDefault(c => c.StartsWith(AuthCookieName + "=", StringComparison.Ordinal))
            : null;

    private static string[] Attributes(string setCookie) =>
        setCookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(a => a.ToLowerInvariant()).ToArray();

    private static void ShouldExpireAuthCookie(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var setCookie = AuthSetCookie(response);
        setCookie.ShouldNotBeNull("logout must send a Set-Cookie for the auth cookie");
        setCookie.ShouldStartWith(AuthCookieName + "=;");

        var attributes = Attributes(setCookie);
        attributes.ShouldContain("path=/");
        var expires = attributes.SingleOrDefault(a => a.StartsWith("expires=", StringComparison.Ordinal));
        expires.ShouldNotBeNull("the cookie must be expired, not just emptied");
        DateTimeOffset.Parse(expires["expires=".Length..]).ShouldBeLessThan(DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task Logout_Anonymous_ExpiresTheAuthCookie()
    {
        using var client = CreateClient("http://localhost");

        using var response = await client.PostAsync(LogoutUrl, null);

        ShouldExpireAuthCookie(response);
    }

    [Test]
    public async Task Logout_AfterBasicAuthSignIn_ExpiresTheHttpOnlyCookieOverHttps()
    {
        using var client = CreateClient("https://localhost");

        // Sign in through Basic auth: the server sets the HttpOnly cookie
        using var signIn = new HttpRequestMessage(HttpMethod.Get, OgcUrl);
        signIn.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.Latin1.GetBytes(
                $"{RegistryWebFactory.AdminUserName}:{RegistryWebFactory.AdminPassword}")));
        using var signInResponse = await client.SendAsync(signIn);
        var cookie = AuthSetCookie(signInResponse);
        cookie.ShouldNotBeNull();
        var token = cookie.Split(';')[0][(AuthCookieName.Length + 1)..];

        // Sign out carrying that cookie, as the browser would
        using var logout = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
        logout.Headers.Add("Cookie", $"{AuthCookieName}={token}");
        using var response = await client.SendAsync(logout);

        ShouldExpireAuthCookie(response);
        // Same attributes it was set with, so the browser replaces that cookie
        Attributes(AuthSetCookie(response)!).ShouldContain("secure");
    }
}
