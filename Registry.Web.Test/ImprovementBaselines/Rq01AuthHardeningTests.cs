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
using Registry.Adapters;
using Registry.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-01 item 6b/6c/6d:
/// <list type="bullet">
/// <item>6b: the unused <c>IPasswordHasher</c>/<c>PasswordHasher</c> pair is gone (Identity hashes passwords).</item>
/// <item>6c: the auth cookie <c>BasicAuthFilter</c> sets is <c>HttpOnly</c>, <c>SameSite=Strict</c> (as the
/// cookie the web client sets) and <c>Secure</c> when the request came over HTTPS.</item>
/// <item>6d: the MonitorToken is compared in constant time; the observable contract is unchanged:
/// the right token passes, a wrong or empty token does not.</item>
/// </list>
/// Run: dotnet test Registry.Web.Test --filter "FullyQualifiedName~Rq01AuthHardeningTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("ImprovementBaseline")]
public class Rq01AuthHardeningTests
{
    private const string MonitorToken = "rq01-monitor-token-0123456789";
    private const string QuickHealthUrl = "/quickhealth";
    private const string AuthCookieName = "jwtToken";

    // An OGC endpoint ([BasicAuthentication]); the dataset need not exist, the filter runs first
    private const string OgcUrl = "/orgs/admin/ds/rq01-missing/wms?service=WMS&request=GetCapabilities";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(new Dictionary<string, string>
        {
            ["AppSettings:MonitorToken"] = MonitorToken
        });
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // 6b

    [Test]
    public void PasswordHasher_IsRemoved()
    {
        typeof(IDdbWrapper).Assembly.GetType("Registry.Ports.IPasswordHasher").ShouldBeNull();
        typeof(FileSystem).Assembly.GetType("Registry.Adapters.PasswordHasher").ShouldBeNull();
    }

    // 6c

    private async Task<string> BasicAuthCookieAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OgcUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.Latin1.GetBytes(
                $"{RegistryWebFactory.AdminUserName}:{RegistryWebFactory.AdminPassword}")));

        using var response = await client.SendAsync(request);

        response.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue("Basic auth must set the auth cookie");
        var cookie = cookies!.SingleOrDefault(c => c.StartsWith(AuthCookieName + "=", StringComparison.Ordinal));
        cookie.ShouldNotBeNull();
        return cookie;
    }

    private static string[] Attributes(string setCookie) =>
        setCookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(a => a.ToLowerInvariant()).ToArray();

    [Test]
    public async Task BasicAuthCookie_OverHttps_IsHttpOnlySecureSameSite()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });

        var attributes = Attributes(await BasicAuthCookieAsync(client));

        attributes.ShouldContain("httponly");
        attributes.ShouldContain("secure");
        attributes.ShouldContain("samesite=strict");
        attributes.ShouldContain("path=/");
    }

    [Test]
    public async Task BasicAuthCookie_OverHttp_IsHttpOnlySameSiteNotSecure()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = false
        });

        var attributes = Attributes(await BasicAuthCookieAsync(client));

        // A Secure cookie set over plain HTTP is dropped by browsers: the dev/http setup must keep working
        attributes.ShouldContain("httponly");
        attributes.ShouldContain("samesite=strict");
        attributes.ShouldNotContain("secure");
    }

    // 6d

    [Test]
    public async Task QuickHealth_RightTokenInQuery_Passes()
    {
        using var response = await _client.GetAsync($"{QuickHealthUrl}?token={MonitorToken}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task QuickHealth_RightTokenAsBearer_Passes()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, QuickHealthUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MonitorToken);

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestCase("wrong-token")]
    [TestCase(MonitorToken + "x")]                  // longer
    [TestCase("rq01-monitor-token-012345678")]      // prefix
    [TestCase("RQ01-MONITOR-TOKEN-0123456789")]     // case differs
    [TestCase("")]
    public async Task QuickHealth_WrongOrEmptyToken_IsRejected(string token)
    {
        using var response = await _client.GetAsync($"{QuickHealthUrl}?token={Uri.EscapeDataString(token)}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task QuickHealth_NoToken_IsRejected()
    {
        using var response = await _client.GetAsync(QuickHealthUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// RQ-01 item 6d: with no MonitorToken configured, no token (not even an empty one) passes.
/// Run: dotnet test Registry.Web.Test --filter "FullyQualifiedName~Rq01NoMonitorTokenTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("ImprovementBaseline")]
public class Rq01NoMonitorTokenTests
{
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [TestCase("")]
    [TestCase("any-token")]
    public async Task QuickHealth_NoConfiguredToken_RejectsEveryToken(string token)
    {
        using var response = await _client.GetAsync($"/quickhealth?token={Uri.EscapeDataString(token)}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task QuickHealth_NoConfiguredToken_AdminStillPasses()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/quickhealth")
            .AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
