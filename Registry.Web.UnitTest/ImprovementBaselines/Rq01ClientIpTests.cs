#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-01 item 3: the client IP behind nginx. Forwarded headers (<c>X-Forwarded-For</c>,
/// <c>X-Forwarded-Proto</c>) are applied first in the pipeline and only when the TCP peer is a
/// configured <c>KnownProxies</c> address (loopback by default), so:
/// <list type="bullet">
/// <item>a spoofed <c>X-Forwarded-For</c> from any other peer does not change the limiter key;</item>
/// <item>the client address forwarded by the proxy becomes the limiter key;</item>
/// <item>configured <c>KnownProxies</c> replace the framework defaults (loopback is no longer trusted);</item>
/// <item>configured <c>KnownNetworks</c> (CIDR) are trusted too: behind a published Docker port the
/// peer is the bridge gateway (<c>172.x.0.1</c>), not loopback; an invalid CIDR stops the start-up;</item>
/// <item><c>X-Forwarded-Proto: https</c> from the proxy makes the request HTTPS: the auth cookie is
/// <c>Secure</c> and HSTS is sent.</item>
/// </list>
/// The limiter key is observed through a recording <see cref="IDownloadLimiter"/> on a preflight
/// download check (real pipeline, real <c>DownloadLimitMiddleware</c>). <see cref="IClientIpResolver"/>,
/// the one source of the client address, and the anonymous rate-limit partition are unit-tested.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq01ClientIpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class Rq01ClientIpTests
{
    private const string ProxyPeer = "127.0.0.1";
    private const string DirectPeer = "203.0.113.5";
    private const string ForwardedClient = "198.51.100.7";
    private const string ConfiguredProxy = "10.0.0.2";

    // Host nginx -> published port 5000 -> container: the peer is the Docker bridge gateway
    private const string DockerGatewayPeer = "172.18.0.1";
    private const string DockerBridgePool = "172.16.0.0/12";
    private const string PublicHost = "http://hub.example.org";

    private const string AuthCookieName = "jwtToken";

    // A download endpoint (DownloadLimitMiddleware); the preflight header answers before the controller
    private const string DownloadUrl = "/orgs/rq01-org/ds/rq01-missing/download";
    private const string PreflightHeader = "X-Download-Check";

    // An OGC endpoint ([BasicAuthentication]); the dataset need not exist, the filter runs first
    private const string OgcUrl = "/orgs/admin/ds/rq01-missing/wms?service=WMS&request=GetCapabilities";

    private RecordingDownloadLimiter _limiter = null!;
    private RegistryWebFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _limiter = new RecordingDownloadLimiter();
        _factory = CreateFactory(_limiter);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory?.Dispose();

    [SetUp]
    public void SetUp() => _limiter.Keys.Clear();

    private static RegistryWebFactory CreateFactory(IDownloadLimiter limiter,
        IDictionary<string, string>? settings = null) =>
        new(settings, services =>
        {
            services.RemoveAll<IDownloadLimiter>();
            services.AddSingleton(limiter);
        });

    private static HttpClient CreateClient(RegistryWebFactory factory, string baseAddress = PublicHost) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            HandleCookies = false,
            AllowAutoRedirect = false
        });

    private static async Task<string> LimiterKeyAsync(RegistryWebFactory factory, RecordingDownloadLimiter limiter,
        string peer, string? forwardedFor)
    {
        using var client = CreateClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl).FromPeer(peer);
        request.Headers.Add(PreflightHeader, "1");
        if (forwardedFor != null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the preflight check must reach the download limiter");
        return limiter.Keys.ShouldHaveSingleItem();
    }

    private static HttpRequestMessage ForwardedHttps(HttpRequestMessage request, string peer)
    {
        request.FromPeer(peer);
        request.Headers.Add("X-Forwarded-Proto", "https");
        return request;
    }

    private async Task<string[]> BasicAuthCookieAttributesAsync(string peer)
    {
        using var client = CreateClient(_factory);
        using var request = ForwardedHttps(new HttpRequestMessage(HttpMethod.Get, OgcUrl), peer);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.Latin1.GetBytes(
                $"{RegistryWebFactory.AdminUserName}:{RegistryWebFactory.AdminPassword}")));

        using var response = await client.SendAsync(request);

        response.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue("Basic auth must set the auth cookie");
        var cookie = cookies!.SingleOrDefault(c => c.StartsWith(AuthCookieName + "=", StringComparison.Ordinal));
        cookie.ShouldNotBeNull();
        return cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(a => a.ToLowerInvariant()).ToArray();
    }

    private async Task<HttpResponseMessage> ForwardedHttpsGetAsync(string peer)
    {
        using var client = CreateClient(_factory);
        using var request = ForwardedHttps(new HttpRequestMessage(HttpMethod.Get, DownloadUrl), peer);
        request.Headers.Add(PreflightHeader, "1");
        return await client.SendAsync(request);
    }

    // Client IP / limiter key

    [Test]
    public async Task SpoofedForwardedFor_FromNonProxyPeer_DoesNotChangeTheLimiterKey()
    {
        var key = await LimiterKeyAsync(_factory, _limiter, DirectPeer, ForwardedClient);

        key.ShouldBe("ip:" + DirectPeer);
    }

    [Test]
    public async Task ForwardedFor_FromProxyPeer_IsTheLimiterKey()
    {
        var key = await LimiterKeyAsync(_factory, _limiter, ProxyPeer, ForwardedClient);

        key.ShouldBe("ip:" + ForwardedClient);
    }

    [Test]
    public async Task ForwardedForChain_FromProxyPeer_TrustsOnlyTheLastHop()
    {
        // One proxy (ForwardLimit 1): the entry nginx appended is the client, anything before it was
        // sent by the client and is not trusted
        var key = await LimiterKeyAsync(_factory, _limiter, ProxyPeer, "192.0.2.66, " + ForwardedClient);

        key.ShouldBe("ip:" + ForwardedClient);
    }

    [Test]
    public async Task NoForwardedFor_FromDirectPeer_IsThePeer()
    {
        var key = await LimiterKeyAsync(_factory, _limiter, DirectPeer, null);

        key.ShouldBe("ip:" + DirectPeer);
    }

    [Test]
    public async Task ConfiguredKnownProxies_ReplaceTheLoopbackDefaults()
    {
        var limiter = new RecordingDownloadLimiter();
        using var factory = CreateFactory(limiter, new Dictionary<string, string>
        {
            ["AppSettings:KnownProxies:0"] = ConfiguredProxy
        });

        var fromLoopback = await LimiterKeyAsync(factory, limiter, ProxyPeer, ForwardedClient);
        limiter.Keys.Clear();
        var fromConfigured = await LimiterKeyAsync(factory, limiter, ConfiguredProxy, ForwardedClient);

        fromLoopback.ShouldBe("ip:" + ProxyPeer, "loopback is no longer a known proxy once KnownProxies is set");
        fromConfigured.ShouldBe("ip:" + ForwardedClient);
    }

    [Test]
    public async Task ConfiguredKnownNetworks_TrustTheDockerGatewayPeer()
    {
        var limiter = new RecordingDownloadLimiter();
        using var factory = CreateFactory(limiter, new Dictionary<string, string>
        {
            ["AppSettings:KnownNetworks:0"] = DockerBridgePool
        });

        var fromGateway = await LimiterKeyAsync(factory, limiter, DockerGatewayPeer, ForwardedClient);
        limiter.Keys.Clear();
        var fromLoopback = await LimiterKeyAsync(factory, limiter, ProxyPeer, ForwardedClient);

        fromGateway.ShouldBe("ip:" + ForwardedClient, "a peer inside a KnownNetworks range is a trusted proxy");
        fromLoopback.ShouldBe("ip:" + ForwardedClient, "KnownNetworks add to KnownProxies (loopback by default)");
    }

    [Test]
    public async Task DockerGatewayPeer_WithoutKnownNetworks_ForwardedForIsIgnored()
    {
        var key = await LimiterKeyAsync(_factory, _limiter, DockerGatewayPeer, ForwardedClient);

        key.ShouldBe("ip:" + DockerGatewayPeer);
    }

    [TestCase("not-a-network")]
    [TestCase("172.16.0.0/33")]
    [TestCase("172.18.0.1")]
    public void InvalidKnownNetwork_StopsTheStartUp(string network)
    {
        using var factory = CreateFactory(new RecordingDownloadLimiter(), new Dictionary<string, string>
        {
            ["AppSettings:KnownNetworks:0"] = network
        });

        // RQ-07: an options validation error (it was an InvalidOperationException from ConfigureServices)
        var error = Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());

        error.Message.ShouldContain("AppSettings:KnownNetworks");
        error.Message.ShouldContain(network);
    }

    // X-Forwarded-Proto: Secure cookie and HSTS behind the proxy

    [Test]
    public async Task ForwardedProtoHttps_FromProxyPeer_MakesTheBasicAuthCookieSecure()
    {
        var attributes = await BasicAuthCookieAttributesAsync(ProxyPeer);

        attributes.ShouldContain("secure");
        attributes.ShouldContain("httponly");
    }

    [Test]
    public async Task ForwardedProtoHttps_FromNonProxyPeer_IsIgnored()
    {
        var attributes = await BasicAuthCookieAttributesAsync(DirectPeer);

        attributes.ShouldNotContain("secure");
    }

    [Test]
    public async Task ForwardedProtoHttps_FromProxyPeer_SendsHsts()
    {
        using var response = await ForwardedHttpsGetAsync(ProxyPeer);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Strict-Transport-Security").ShouldBeTrue("HSTS must be sent over HTTPS behind the proxy");
    }

    [Test]
    public async Task ForwardedProtoHttps_FromNonProxyPeer_SendsNoHsts()
    {
        using var response = await ForwardedHttpsGetAsync(DirectPeer);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Strict-Transport-Security").ShouldBeFalse();
    }

    // IClientIpResolver and the rate-limit partition

    private static DefaultHttpContext Anonymous(string? peer, IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext();
        if (peer != null)
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        if (services != null)
            context.RequestServices = services;
        return context;
    }

    [Test]
    public void Resolver_UsesTheConnectionAddress_NeverTheRawForwardedHeader()
    {
        var context = Anonymous(DirectPeer);
        context.Request.Headers["X-Forwarded-For"] = ForwardedClient;

        var resolver = new ClientIpResolver();

        resolver.Resolve(context).ShouldBe(IPAddress.Parse(DirectPeer));
        resolver.AnonymousKey(context).ShouldBe("ip:" + DirectPeer);
    }

    [Test]
    public void Resolver_ReportsIPv4MappedAddressesAsIPv4()
    {
        var resolver = new ClientIpResolver();

        resolver.AnonymousKey(Anonymous("::ffff:" + DirectPeer)).ShouldBe("ip:" + DirectPeer);
    }

    [Test]
    public void Resolver_UnknownAddress_HasOneExplicitKey()
    {
        var resolver = new ClientIpResolver();

        resolver.Resolve(Anonymous(null)).ShouldBeNull();
        resolver.AnonymousKey(Anonymous(null)).ShouldBe("ip:unknown");
    }

    [Test]
    public void UploadProbePartition_Anonymous_KeysOnTheResolvedClient()
    {
        // The registered resolver (real container): anonymous clients get one budget each
        var services = _factory.Services;

        var direct = RateLimitPolicies.UploadProbePartition(Anonymous(DirectPeer, services));
        var mapped = RateLimitPolicies.UploadProbePartition(Anonymous("::ffff:" + DirectPeer, services));
        var other = RateLimitPolicies.UploadProbePartition(Anonymous(ForwardedClient, services));

        direct.PartitionKey.ShouldBe("ip:" + DirectPeer);
        mapped.PartitionKey.ShouldBe(direct.PartitionKey);
        other.PartitionKey.ShouldBe("ip:" + ForwardedClient);
    }

    /// <summary>Enabled limiter that grants every slot and records the keys it is asked about.</summary>
    private sealed class RecordingDownloadLimiter : IDownloadLimiter
    {
        public ConcurrentQueue<string> Keys { get; } = new();

        public bool IsEnabled => true;

        public bool TryAcquireSlot(string key)
        {
            Keys.Enqueue(key);
            return true;
        }

        public void ReleaseSlot(string key)
        {
        }

        public int GetActiveDownloads(string key) => 0;

        public bool CanAcquireSlot(string key)
        {
            Keys.Enqueue(key);
            return true;
        }
    }
}
