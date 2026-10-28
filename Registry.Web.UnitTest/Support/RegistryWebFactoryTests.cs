using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Ports;
using Shouldly;

namespace Registry.Web.Test.Support;

[TestFixture]
[NonParallelizable]
public class RegistryWebFactoryTests
{
    private const string FeaturesUrl = "/sys/features";
    private const string RefreshUrl = "/users/authenticate/refresh";
    private const string UsersUrl = "/users";

    private RegistryWebFactory _factory;
    private HttpClient _client;

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

    [Test]
    public async Task Anonymous_PublicEndpoint_Succeeds()
    {
        using var response = await _client.GetAsync(FeaturesUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Anonymous_AuthenticatedEndpoint_IsChallenged()
    {
        using var response = await _client.PostAsync(RefreshUrl, null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task TestUserHeader_DefaultAdmin_CarriesIdentity()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl)
            .AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadUserNameAsync(response)).ShouldBe(RegistryWebFactory.AdminUserName);
    }

    [Test]
    public async Task TestUserHeader_RegularUser_CarriesIdentityWithoutAdminRights()
    {
        const string userName = "factory-smoke-user";
        await _factory.CreateUserAsync(userName);

        using (var refresh = new HttpRequestMessage(HttpMethod.Post, RefreshUrl).AsUser(userName))
        using (var response = await _client.SendAsync(refresh))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ReadUserNameAsync(response)).ShouldBe(userName);
        }

        using (var adminOnly = new HttpRequestMessage(HttpMethod.Get, UsersUrl).AsUser(userName))
        using (var response = await _client.SendAsync(adminOnly))
            response.StatusCode.ShouldNotBe(HttpStatusCode.OK);

        using (var asAdmin = new HttpRequestMessage(HttpMethod.Get, UsersUrl)
                   .AsUser(RegistryWebFactory.AdminUserName, isAdmin: true))
        using (var response = await _client.SendAsync(asAdmin))
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task TestUserHeader_UnknownUser_IsRejected()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl).AsUser("no-such-user");

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task AuthorizationHeader_WithoutTestUser_IsHandledByJwtScheme()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RefreshUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-valid-token");

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");
    }

    [Test]
    public void DdbWrapper_IsTheFake()
    {
        _factory.Services.GetRequiredService<IDdbWrapper>().ShouldBeSameAs(_factory.DdbWrapper.Object);
    }

    [Test]
    public async Task PeerIpHeader_SetsRemoteIpAddress_BeforeThePipeline()
    {
        using var factory = new RegistryWebFactory(configureServices: services =>
            services.AddSingleton<IStartupFilter, EchoPeerStartupFilter>());
        using var client = factory.CreateClient();

        using (var withPeer = new HttpRequestMessage(HttpMethod.Get, EchoPeerStartupFilter.Path)
                   .FromPeer("203.0.113.5"))
        using (var response = await client.SendAsync(withPeer))
            (await response.Content.ReadAsStringAsync()).ShouldBe("203.0.113.5");

        using (var withoutPeer = new HttpRequestMessage(HttpMethod.Get, EchoPeerStartupFilter.Path))
        using (var response = await client.SendAsync(withoutPeer))
            (await response.Content.ReadAsStringAsync()).ShouldBe(EchoPeerStartupFilter.NoPeer);
    }

    private static async Task<string?> ReadUserNameAsync(HttpResponseMessage response)
    {
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        return body.GetValue("username", StringComparison.OrdinalIgnoreCase)?.Value<string>();
    }

    /// <summary>
    /// Registered after the factory's filter, so it sees the address that filter set.
    /// </summary>
    private sealed class EchoPeerStartupFilter : IStartupFilter
    {
        public const string Path = "/__test/peer";
        public const string NoPeer = "<none>";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(Path, branch => branch.Run(context =>
                context.Response.WriteAsync(context.Connection.RemoteIpAddress?.ToString() ?? NoPeer)));
            next(app);
        };
    }
}
