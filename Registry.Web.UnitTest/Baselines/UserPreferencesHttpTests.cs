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
/// <c>GET</c> and <c>PATCH /users/preferences</c> through the whole pipeline (<see cref="RegistryWebFactory"/>):
/// <list type="bullet">
/// <item>anonymous callers get 401; the caller is always the user, never a route or body value;</item>
/// <item>a <c>PATCH</c> answers 200 with all the sections and a <c>GET</c> reads them back; a bad body is a 400 (never a 500),
/// a declared body above 64 KB a 413 that names the preferences, and neither writes anything;</item>
/// <item>the literal route shadows nothing: <c>PUT</c> and <c>DELETE /users/preferences</c> still reach the
/// <c>{userName}</c> actions for a user with that name, and <c>GET /users/meta</c> does not carry the preferences.</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserPreferencesHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class UserPreferencesHttpTests : IDisposable
{
    private const string Alice = "prefshttp-alice";
    private const string Bob = "prefshttp-bob";
    private const string PreferencesUser = "preferences";
    private const string Url = "/users/preferences";
    private const int Limit = 65536;

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await _factory.CreateUserAsync(Alice);
        await _factory.CreateUserAsync(Bob);
        await _factory.CreateUserAsync(PreferencesUser);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>Every test starts from a user without sections.</summary>
    [SetUp]
    public async Task SetUp()
    {
        foreach (var user in new[] { Alice, Bob })
        {
            var (_, current) = await Send(HttpMethod.Get, user);
            var names = ((JsonObject)current!).Select(p => p.Key).ToArray();
            if (names.Length == 0)
                continue;
            var clear = new JsonObject();
            foreach (var name in names)
                clear[name] = null;
            (await Send(HttpMethod.Patch, user, clear.ToJsonString())).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    // ---- 401 ----

    [Test]
    public async Task Get_Anonymous_Is401()
    {
        var (response, body) = await Send(HttpMethod.Get, user: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, body?.ToJsonString());
    }

    [Test]
    public async Task Patch_Anonymous_Is401_AndWritesNothing()
    {
        var (response, _) = await Send(HttpMethod.Patch, user: null, """{"map":{"zoom":3}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, Alice)).Body!.ToJsonString().ShouldBe("{}");
    }

    // ---- round trip ----

    [Test]
    public async Task Get_ANewUser_Is200WithAnEmptyObject()
    {
        var (response, body) = await Send(HttpMethod.Get, Alice);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        body.ShouldBeOfType<JsonObject>().Count.ShouldBe(0);
    }

    [Test]
    public async Task Patch_Is200WithAllTheSections_AndGetReturnsThem()
    {
        const string hubBody = """
            {"units":{"measurement":"metric"},"map":{"zoom":12.5,"layers":["osm","sat"],"center":[11.25,45]},"text":"Zoë 日本"}
            """;

        var (patched, patchBody) = await Send(HttpMethod.Patch, Alice, hubBody);
        var (got, getBody) = await Send(HttpMethod.Get, Alice);

        patched.StatusCode.ShouldBe(HttpStatusCode.OK, patchBody?.ToJsonString());
        got.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.DeepEquals(getBody, JsonNode.Parse(hubBody)).ShouldBeTrue(getBody?.ToJsonString());
        JsonNode.DeepEquals(patchBody, getBody).ShouldBeTrue("the PATCH answers what a GET returns afterwards");
    }

    [Test]
    public async Task Patch_ASecondPatch_MergesAndANullDeletes()
    {
        await Send(HttpMethod.Patch, Alice, """{"units":{"measurement":"metric"},"map":{"zoom":3}}""");

        var (response, body) = await Send(HttpMethod.Patch, Alice, """{"map":null,"orgs":{"list":{"sort":"name"}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!.ToJsonString().ShouldBe("""{"units":{"measurement":"metric"},"orgs":{"list":{"sort":"name"}}}""");
        (await Send(HttpMethod.Get, Alice)).Body!.ToJsonString().ShouldBe(body.ToJsonString());
    }

    [Test]
    public async Task Patch_Numbers_KeepWholeAndFractionalValuesApart()
    {
        await Send(HttpMethod.Patch, Alice, """{"map":{"zoom":12.5,"count":3,"big":9007199254740993}}""");

        var (_, body) = await Send(HttpMethod.Get, Alice);

        body!["map"]!["count"]!.ToJsonString().ShouldBe("3");
        body["map"]!["zoom"]!.ToJsonString().ShouldBe("12.5");
        body["map"]!["big"]!.ToJsonString().ShouldBe("9007199254740993");
    }

    [Test]
    public async Task Preferences_BelongToTheCaller_NotToAnotherUser()
    {
        await Send(HttpMethod.Patch, Alice, """{"map":{"zoom":3}}""");

        (await Send(HttpMethod.Get, Bob)).Body!.ToJsonString().ShouldBe("{}");
        (await Send(HttpMethod.Get, Alice)).Body!.ToJsonString().ShouldBe("""{"map":{"zoom":3}}""");
    }

    [Test]
    public async Task Patch_ARolesSection_IsStored_AndGrantsNoAdminAccess()
    {
        var (response, body) = await Send(HttpMethod.Patch, Bob, """{"roles":["admin"]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body!.ToJsonString().ShouldBe("""{"roles":["admin"]}""");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/users/detailed").AsUser(Bob);
        using var admin = await _client.SendAsync(request);
        admin.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- GET /users/meta ----

    [Test]
    public async Task UserMeta_DoesNotContainThePreferences()
    {
        await Send(HttpMethod.Patch, Alice, """{"map":{"zoom":3},"roles":["admin"]}""");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/users/meta").AsUser(Alice);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        text.ShouldNotContain("zoom");
        text.ShouldNotContain("preferences", Case.Insensitive);
        ((JsonObject)JsonNode.Parse(text)!).ContainsKey("map").ShouldBeFalse();
        ((JsonObject)JsonNode.Parse(text)!).ContainsKey("roles").ShouldBeFalse();
    }

    // ---- bad bodies are 400, never 500 ----

    [TestCase("[]", TestName = "Patch_AnArray_Is400")]
    [TestCase("[{\"map\":1}]", TestName = "Patch_AnArrayOfObjects_Is400")]
    [TestCase("\"text\"", TestName = "Patch_AString_Is400")]
    [TestCase("5", TestName = "Patch_ANumber_Is400")]
    [TestCase("true", TestName = "Patch_ABoolean_Is400")]
    [TestCase("null", TestName = "Patch_TheJsonNull_Is400")]
    [TestCase("", TestName = "Patch_AnEmptyBody_Is400")]
    [TestCase("{", TestName = "Patch_BrokenJson_Is400")]
    [TestCase("""{"Map":{"zoom":3}}""", TestName = "Patch_AnUppercaseSectionName_Is400")]
    [TestCase("""{"a-b":1}""", TestName = "Patch_ASectionNameWithADash_Is400")]
    [TestCase("""{"":1}""", TestName = "Patch_AnEmptySectionName_Is400")]
    [TestCase("""{"map\n":1}""", TestName = "Patch_ASectionNameWithATrailingNewline_Is400")]
    [TestCase("""{"a123456789a123456789a123456789abc":1}""", TestName = "Patch_A33CharacterSectionName_Is400")]
    [TestCase("""{"map":{"zoom":1e400}}""", TestName = "Patch_ANumberBeyondTheDoubleRange_Is400")]
    public async Task Patch_ABadBody_Is400ProblemDetails_AndWritesNothing(string body)
    {
        await Send(HttpMethod.Patch, Alice, """{"keep":{"v":1}}""");

        var (response, problem) = await Send(HttpMethod.Patch, Alice, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem?.ToJsonString());
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        problem.ShouldNotBeNull();
        problem["status"]!.GetValue<int>().ShouldBe(400);
        (await Send(HttpMethod.Get, Alice)).Body!.ToJsonString().ShouldBe("""{"keep":{"v":1}}""");
    }

    [Test]
    public async Task Patch_NestedDeeperThanTheLimit_Is400_AndTheLimitItself_Is200()
    {
        static string Nested(int levels)
        {
            var text = "1";
            for (var i = 0; i < levels; i++)
                text = "{\"a\":" + text + "}";
            return text;
        }

        var (ok, _) = await Send(HttpMethod.Patch, Alice, "{\"map\":" + Nested(8) + "}");
        var (tooDeep, problem) = await Send(HttpMethod.Patch, Alice, "{\"map\":" + Nested(9) + "}");

        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        tooDeep.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem?.ToJsonString());
        problem!["detail"]!.GetValue<string>().ShouldContain("deeper than 8");
    }

    [Test]
    public async Task Patch_33Sections_Is400()
    {
        var body = new JsonObject();
        for (var i = 0; i < 33; i++)
            body["s" + i] = 1;

        var (response, problem) = await Send(HttpMethod.Patch, Alice, body.ToJsonString());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem?.ToJsonString());
        (await Send(HttpMethod.Get, Alice)).Body!.ToJsonString().ShouldBe("{}");
    }

    [Test]
    public async Task Patch_ABodyUnderTheRequestLimit_WhoseMergedValueIsOverTheStoredLimit_Is400()
    {
        // 40,000 + 40,000 characters: each request is under 64 KB, the two sections together are not
        (await Send(HttpMethod.Patch, Alice, "{\"first\":\"" + new string('x', 40_000) + "\"}")).Response.StatusCode
            .ShouldBe(HttpStatusCode.OK);

        var (response, problem) = await Send(HttpMethod.Patch, Alice, "{\"second\":\"" + new string('y', 40_000) + "\"}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, problem?.ToJsonString());
        problem!["detail"]!.GetValue<string>().ShouldContain("65536");
        ((JsonObject)(await Send(HttpMethod.Get, Alice)).Body!).ContainsKey("second").ShouldBeFalse();
    }

    [Test]
    public async Task Patch_AnotherMediaType_Is415()
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, Url) { Content = new StringContent("{}", Encoding.UTF8, "text/plain") }
            .AsUser(Alice);
        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    // ---- 413 ----

    [Test]
    public async Task Patch_ADeclaredBodyAbove64KB_Is413_NamingThePreferences_AndWritesNothing()
    {
        await Send(HttpMethod.Patch, Alice, """{"keep":{"v":1}}""");
        var body = "{\"map\":\"" + new string('x', Limit) + "\"}";

        var (response, problem) = await Send(HttpMethod.Patch, Alice, body);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, problem?.ToJsonString());
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        problem!["detail"]!.GetValue<string>().ShouldBe("Preferences can be at most 65536 bytes");
        (await Send(HttpMethod.Get, Alice)).Body!.ToJsonString().ShouldBe("""{"keep":{"v":1}}""");
    }

    [Test]
    public async Task Patch_ABodyOfExactlyTheLimit_IsNotATooLargeRequest()
    {
        const string prefix = "{\"a\":\"";
        const string suffix = "\"}";
        var body = prefix + new string('x', Limit - prefix.Length - suffix.Length) + suffix;
        Encoding.UTF8.GetByteCount(body).ShouldBe(Limit);

        var (response, problem) = await Send(HttpMethod.Patch, Alice, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, problem?.ToJsonString());
    }

    [Test]
    public async Task Patch_ABodyOneByteOverTheLimit_Is413()
    {
        const string prefix = "{\"a\":\"";
        const string suffix = "\"}";
        var body = prefix + new string('x', Limit - prefix.Length - suffix.Length + 1) + suffix;

        var (response, _) = await Send(HttpMethod.Patch, Alice, body);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    // ---- the literal route shadows no {userName} route ----

    [Test]
    public async Task Put_ForAUserNamedPreferences_StillRoutesToUpdateUser()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url)
        {
            Content = new StringContent("""{"email":"preferences-new@example.com","roles":[]}""", Encoding.UTF8, "application/json")
        }.AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        using var users = new HttpRequestMessage(HttpMethod.Get, "/users/detailed")
            .AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);
        using var list = await _client.SendAsync(users);
        var detailed = (JsonArray)JsonNode.Parse(await list.Content.ReadAsStringAsync())!;
        detailed.ShouldContain(u => (string?)u!["userName"] == PreferencesUser && (string?)u["email"] == "preferences-new@example.com");
    }

    [Test]
    public async Task Put_ForAUserNamedPreferences_ByANonAdmin_IsTheUserNameActionsForbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Url)
        {
            Content = new StringContent("""{"email":"x@example.com","roles":[]}""", Encoding.UTF8, "application/json")
        }.AsUser(Alice);
        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Delete_OnThePreferencesRoute_IsTheUserNameActionsForbiddenForANonAdmin_NotA405()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Url).AsUser(Alice);
        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- helpers ----

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> Send(HttpMethod method, string? user,
        string? json = null)
    {
        using var request = new HttpRequestMessage(method, Url);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        if (user != null)
            request.AsUser(user);

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? body = null;
        if (text.Length > 0 && (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')))
            body = JsonNode.Parse(text);
        return (response, body);
    }
}
