#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-02 item 6 (Docker healthcheck): the compose healthcheck curled <c>/quickhealth</c>, which requires the
/// admin/monitor token, so the containers never became healthy and the nodes waiting on
/// <c>service_healthy</c> never started. Now every HTTP host (web server and thumbnail generator) maps
/// the anonymous liveness probe <see cref="MagicStrings.LivenessUrl"/> through the one
/// <c>LivenessEndpoint.MapLiveness</c>: <c>200 ok</c>, not cached, no details and no DB/native checks.
/// <c>/quickhealth</c> keeps its token guard. <c>docker/healthcheck.sh</c> probes this URL.
/// Run: dotnet test Registry.Web.Test --filter "FullyQualifiedName~Rq02LivenessTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("ImprovementBaseline")]
public class Rq02LivenessTests
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

    /// <summary>The web host answers the probe anonymously with the fixed body and no caching.</summary>
    [Test]
    public async Task WebHost_Liveness_Anonymous_Returns200Ok()
    {
        using var response = await _client.GetAsync(MagicStrings.LivenessUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("ok");
        response.Headers.CacheControl?.NoStore.ShouldBe(true);
    }

    /// <summary>The detailed probes stay behind the admin/monitor token.</summary>
    [TestCase("/quickhealth")]
    [TestCase("/health")]
    public async Task WebHost_GuardedHealth_Anonymous_StillRejected(string url)
    {
        using var response = await _client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The thumbnail generator host (same routes as <c>Program.RunAsThumbnailGenerator</c>) answers the
    /// probe anonymously and still guards <c>POST /generate-thumbnail</c> with its shared token.
    /// </summary>
    [Test]
    public async Task ThumbnailHost_Liveness_Anonymous_Returns200Ok_AndThumbnailRouteStaysGuarded()
    {
        var ddb = new Mock<IDdbWrapper>(MockBehavior.Strict);
        var handler = new ThumbnailRequestHandler("rq02-thumbnail-token-0123456789",
            [System.IO.Path.GetTempPath()], ddb.Object, NullLogger.Instance);

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => Program.MapThumbnailGeneratorEndpoints(endpoints, handler));
                }))
            .StartAsync();
        using var client = host.GetTestClient();

        using var live = await client.GetAsync(MagicStrings.LivenessUrl);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await live.Content.ReadAsStringAsync()).ShouldBe("ok");

        using var thumbnail = await client.PostAsync(ThumbnailRequestHandler.Route,
            new FormUrlEncodedContent([new("path", "/etc/passwd"), new("size", "64")]));
        thumbnail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Strict mock: neither request reached native ddb
        ddb.VerifyNoOtherCalls();
    }
}
