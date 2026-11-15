using System;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Registry.Web.Data;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// What a path that no endpoint matches answers: a GET or a HEAD gets the Hub's page (its client routes
/// such as <c>/login</c>), any other method a 404 ProblemDetails (the SPA default page answered them with a 500), and a
/// path of the API (<c>/orgs</c>, <c>/users</c>, <c>/sys</c>, ...) a 404 ProblemDetails whatever the method, so a client of
/// the API never reads the Hub's HTML as an answer. A slug in any letter case routes and is lowercased.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~HubFallbackHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class HubFallbackHttpTests : IDisposable
{
    private const string HubPage = "<html>hub</html>";
    private const string UnknownPath = "/hub-fallback/no-such-endpoint";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();

        // The Hub's build, where the host looks for it (the content root), before the host starts
        var hub = Path.Combine(_factory.RootPath, MagicStrings.SpaRoot);
        Directory.CreateDirectory(hub);
        File.WriteAllText(Path.Combine(hub, "index.html"), HubPage);

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Get_OfAnUnknownPath_ServesTheHubPage()
    {
        using var response = await _client.GetAsync(UnknownPath);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status200OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(HubPage);
    }

    [Test]
    public async Task Head_OfAnUnknownPath_AnswersLikeTheHubPage()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, UnknownPath);
        using var response = await _client.SendAsync(request);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status200OK);
    }

    [TestCase("POST")]
    [TestCase("PUT")]
    [TestCase("PATCH")]
    [TestCase("DELETE")]
    public async Task AnotherMethod_OnAnUnknownPath_Is404ProblemDetails(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), UnknownPath)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status404NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        problem["status"]!.GetValue<int>().ShouldBe(StatusCodes.Status404NotFound);
        problem["detail"]!.GetValue<string>().ShouldBe($"No endpoint answers {method} {UnknownPath}");
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }
    [TestCase("/")]
    [TestCase("/login")]
    [TestCase("/r")]
    [TestCase("/r/Org/ds")]
    [TestCase("/r/org/ds/view/abc/map")]
    [TestCase("/upload")]
    [TestCase("/admin/users")]
    [TestCase("/account")]
    public async Task TheClientRoutesOfTheHub_GetTheHubPage(string path)
    {
        using var response = await _client.GetAsync(path);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status200OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(HubPage);
    }

    [TestCase("/orgs/nope-x/unknown")]
    [TestCase("/orgs/nope-x/ds/nope-y/no-such-endpoint")]
    [TestCase("/users/no-such-endpoint/at-all")]
    [TestCase("/SYS/no-such-endpoint")]
    [TestCase("/share/no-such-endpoint")]
    [TestCase("/stac/no-such-endpoint")]
    public async Task Get_OfAnUnmatchedPathOfTheApi_Is404ProblemDetails_NotTheHubPage(string path)
    {
        using var response = await _client.GetAsync(path);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status404NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        problem["detail"]!.GetValue<string>().ShouldBe($"No endpoint answers GET {path}");
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task ASlugInAnyLetterCase_IsRoutedAndLowercased()
    {
        var owner = await _factory.CreateUserAsync("slug-owner");
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            Seed.Organization(context, "slug-case-org", ownerId: owner.Id);
            await context.SaveChangesAsync();
        }

        // The listing and the organization itself: the slug of the answer is the stored one
        using var listing = await Send("/orgs/SLUG-Case-ORG/ds", owner.UserName!);
        listing.StatusCode.ShouldBe(HttpStatusCode.OK);
        listing.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        JsonNode.Parse(await listing.Content.ReadAsStringAsync()).ShouldBeOfType<JsonArray>();

        using var organization = await Send("/orgs/SLUG-Case-ORG", owner.UserName!);
        organization.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await organization.Content.ReadAsStringAsync())!["slug"]!.GetValue<string>()
            .ShouldBe("slug-case-org");
    }

    private Task<HttpResponseMessage> Send(string path, string userName) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).AsUser(userName));
}
