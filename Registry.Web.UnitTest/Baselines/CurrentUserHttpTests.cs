using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>GET /users/me</c> through the whole pipeline: every signed-in user, administrator or not, reads their own name,
/// e-mail, roles and the organizations they own or belong to (the Account page of the Hub); the routes that take a user
/// name stay for administrators, so a non-administrator has no other way to read their own e-mail or organizations.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~CurrentUserHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class CurrentUserHttpTests : IDisposable
{
    private const string Alice = "meuser-alice";
    private const string Bob = "meuser-bob";
    private const string Url = "/users/me";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await _factory.CreateUserAsync(Alice);
        await _factory.CreateUserAsync(Bob);

        // Alice owns one organization and is a member of one of Bob's; Bob's other one is not hers
        await CreateOrganization(Alice, "me-alice-owned");
        await CreateOrganization(Bob, "me-bob-shared");
        await CreateOrganization(Bob, "me-bob-private");
        using var join = new HttpRequestMessage(HttpMethod.Post, $"/users/{Alice}/orgs")
        {
            Content = JsonContent.Create(new { orgSlug = "me-bob-shared", permissions = 0 })
        }.AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);
        using var joined = await _client.SendAsync(join);
        joined.StatusCode.ShouldBe(HttpStatusCode.OK, await joined.Content.ReadAsStringAsync());
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Anonymous_Is401()
    {
        using var response = await _client.GetAsync(Url);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task NonAdmin_ReadsTheirOwnEmailRolesAndOrganizations()
    {
        var (response, body) = await Get(Alice);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["userName"]!.GetValue<string>().ShouldBe(Alice);
        body["email"]!.GetValue<string>().ShouldBe($"{Alice}@example.com");
        Strings(body["roles"]).ShouldBeEmpty();
        // Owned and joined ones, not the public organization every user sees, nor the ones of others
        Strings(body["organizations"]).ShouldBe(["me-alice-owned", "me-bob-shared"]);
    }

    [Test]
    public async Task NonAdmin_CannotReadTheRoutesThatTakeAUserName_EvenTheirOwn()
    {
        foreach (var route in new[] { $"/users/{Alice}/orgs", $"/users/{Alice}/storage", "/users/detailed" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route).AsUser(Alice);
            using var response = await _client.SendAsync(request);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, route);
        }
    }

    [Test]
    public async Task Admin_ReadsTheirOwnProfile_WithTheAdminRole_NotEveryOrganization()
    {
        var (response, body) = await Get(RegistryWebFactory.AdminUserName);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body!["userName"]!.GetValue<string>().ShouldBe(RegistryWebFactory.AdminUserName);
        body["email"]!.GetValue<string>().ShouldBe(RegistryWebFactory.AdminEmail);
        Strings(body["roles"]).ShouldContain("admin");
        Strings(body["organizations"]).ShouldNotContain("me-bob-private");
    }

    // ---- helpers ----

    private async Task CreateOrganization(string owner, string slug)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orgs")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["slug"] = slug, ["name"] = slug, ["isPublic"] = "false"
            })
        }.AsUser(owner);
        using var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> Get(string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url).AsUser(user, isAdmin: user == RegistryWebFactory.AdminUserName);
        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.TrimStart().StartsWith('{') ? JsonNode.Parse(text) : null);
    }

    private static string[] Strings(JsonNode? node) =>
        [.. ((JsonArray)node!).Select(item => item!.GetValue<string>())];
}
