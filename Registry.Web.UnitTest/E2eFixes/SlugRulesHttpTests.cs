using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// Finding 3 through HTTP: a one-character slug is a 400 that says the rule (it was a stored organization and a 500),
/// and a two-character one is created and routes (the <c>Location</c> of the 201 resolves).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~SlugRulesHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SlugRulesHttpTests : IDisposable
{
    private const string User = "slug-rules-user";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await _factory.CreateUserAsync(User);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(method, url).AsUser(User);
        if (form != null)
            request.Content = new FormUrlEncodedContent(form);
        return await _client.SendAsync(request);
    }

    private int StoredOrganizations(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<RegistryContext>().Organizations.Count(o => o.Slug == slug);
    }

    [Test]
    public async Task CreateOrganization_WithOneCharacterSlug_Is400WithTheRule_AndStoresNothing()
    {
        using var response = await SendAsync(HttpMethod.Post, "/orgs",
            new() { ["slug"] = "o", ["name"] = "One character" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var detail = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["detail"]!.GetValue<string>();
        detail.ShouldContain("Organization slug is invalid");
        detail.ShouldContain("2 to 128");
        StoredOrganizations("o").ShouldBe(0);
    }

    [Test]
    public async Task CreateOrganization_WithTwoCharacters_Is201_AndTheLocationRoutes()
    {
        using var created = await SendAsync(HttpMethod.Post, "/orgs",
            new() { ["slug"] = "ok", ["name"] = "Two characters" });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        created.Headers.Location!.ToString().ShouldEndWith("/orgs/ok");

        using var read = await SendAsync(HttpMethod.Get, created.Headers.Location.PathAndQuery);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await read.Content.ReadAsStringAsync())!["slug"]!.GetValue<string>().ShouldBe("ok");
    }

    [Test]
    public async Task CreateDataset_WithOneCharacterSlug_Is400WithTheRule()
    {
        using var org = await SendAsync(HttpMethod.Post, "/orgs", new() { ["slug"] = "dsorg", ["name"] = "Org" });
        org.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await SendAsync(HttpMethod.Post, "/orgs/dsorg/ds", new() { ["slug"] = "d" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var detail = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["detail"]!.GetValue<string>();
        detail.ShouldContain("Dataset slug is invalid");
        detail.ShouldContain("2 to 128");
    }
}
