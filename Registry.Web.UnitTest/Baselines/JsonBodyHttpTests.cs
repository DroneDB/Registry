#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// A JSON body through the whole pipeline (route, authorization, the input formatter, validation, the manager,
/// the output formatter) on <see cref="RegistryWebFactory"/>. A body the formatter cannot read is a 400 problem, never a
/// 500; a body of the wrong media type is refused; what the Hub sends is bound and answered. These are the answers
/// the old stack gave too: the same tests ran green on the Json.NET host before the switch.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~JsonBodyHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class JsonBodyHttpTests : IDisposable
{
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private int _users;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<(HttpStatusCode Status, string? ContentType, JsonNode? Body)> PostUser(string body,
        string mediaType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users")
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue(mediaType))
        };
        using var response = await _client.SendAsync(request.AsUser(RegistryWebFactory.AdminUserName, isAdmin: true));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType,
            text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private string NewUser(string extra = "") =>
        $$"""{"userName":"jsonuser{{++_users}}","email":"u{{_users}}@example.com","password":"Password1234","roles":[]{{extra}}}""";

    [Test]
    public async Task WhatTheHubSends_IsBound_AndAnswered()
    {
        var (status, contentType, body) = await PostUser(NewUser());

        status.ShouldBe(HttpStatusCode.OK);
        contentType.ShouldBe("application/json");
        body!["userName"]!.GetValue<string>().ShouldStartWith("jsonuser");
    }

    [Test]
    public async Task MemberNames_AreReadInAnyCase_AndUnknownMembersAreIgnored()
    {
        var (status, _, body) = await PostUser(
            """{"USERNAME":"jsoncase","Email":"case@example.com","password":"Password1234","roles":[],"neverHeardOfIt":{"a":1}}""");

        status.ShouldBe(HttpStatusCode.OK);
        body!["userName"]!.GetValue<string>().ShouldBe("jsoncase");
    }

    [TestCase("{")]
    [TestCase("not json")]
    [TestCase("[1,2")]
    [TestCase("""{"userName":"x","password":"Password1234","roles":"admin"}""")]
    [TestCase("""{"userName":{"a":1},"password":"Password1234"}""")]
    public async Task ABodyTheFormatterCannotRead_Is400(string body)
    {
        var (status, contentType, problem) = await PostUser(body);

        status.ShouldBe(HttpStatusCode.BadRequest);
        contentType.ShouldBe("application/problem+json");
        problem!["status"]!.GetValue<int>().ShouldBe(400);
    }

    [Test]
    public async Task ABodyWithoutARequiredMember_Is400_AndNamesTheMember()
    {
        var (status, _, problem) = await PostUser("""{"email":"a@b.c","roles":[]}""");

        status.ShouldBe(HttpStatusCode.BadRequest);
        problem!["errors"]!.AsObject().Select(e => e.Key.ToLowerInvariant()).ShouldContain("username");
    }

    [Test]
    public async Task AnEmptyBody_Is400()
    {
        var (status, _, _) = await PostUser("");

        status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TestCase("text/plain")]
    [TestCase("application/xml")]
    public async Task AnotherMediaType_Is415(string mediaType)
    {
        var (status, _, _) = await PostUser(NewUser(), mediaType);

        status.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Test]
    public async Task AJsonMediaTypeWithASuffix_IsRead()
    {
        var (status, _, _) = await PostUser(NewUser(), "application/vnd.registry+json");

        status.ShouldBe(HttpStatusCode.OK);
    }
}
