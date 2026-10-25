#nullable enable
using System;
using System.Collections.Concurrent;
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
/// End-to-end check: the obsolete <c>POST chattr {"public":true}</c>
/// answered 200 but changed nothing: it wrote the legacy <c>public</c> meta key, and access reads <c>visibility</c>.
/// It is still routed (the DroneDB CLI's old <c>ddb chattr</c> reaches it), so it is a compatibility alias of the
/// visibility change now: <c>public: true</c> sets the visibility to Public, <c>false</c> to Private, through the
/// same code as <c>PUT</c> of the dataset (the meta write and the listings' invalidation).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ChattrHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ChattrHttpTests : IDisposable
{
    private const string OwnerUser = "chattr-owner";
    private const string OtherUser = "chattr-other";
    private const string Org = "chattr-org";
    private const string Ds = "chattr-ds";

    private readonly ConcurrentDictionary<string, JsonNode?> _meta = new();
    private readonly List<(string Key, string Data)> _writes = [];
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // The dataset root meta of libddb: a missing key throws, a written one is read back
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string _, string key, string? _) =>
                _meta.TryGetValue(key, out var data)
                    ? new JsonObject { ["id"] = key, ["data"] = data?.DeepClone(), ["mtime"] = 1700000000 }.ToJsonString()
                    : throw new DdbException($"Key not found: {key}"));
        _factory.DdbWrapper.Setup(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string _, string key, string data, string? _) =>
            {
                lock (_writes)
                    _writes.Add((key, data));
                _meta[key] = JsonNode.Parse(data);
                return new Meta { Id = key, Data = TestJson.Parse(data), ModifiedTime = DateTime.UtcNow };
            });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        await _factory.CreateUserAsync(OtherUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _meta.Clear();
        _meta["name"] = JsonValue.Create("Chattr dataset");
        lock (_writes)
            _writes.Clear();
    }

    private async Task<HttpResponseMessage> ChattrAsync(string attributes, string? user = OwnerUser)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/chattr")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["attrs"] = attributes })
        };
        return await _client.SendAsync(user == null ? request : request.AsUser(user));
    }

    private async Task<HttpStatusCode> AnonymousReadAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/meta/get/name");
        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    [Test]
    public async Task Public_True_MakesTheDatasetPublic_ThroughTheVisibilityMeta()
    {
        (await AnonymousReadAsync()).ShouldBe(HttpStatusCode.Unauthorized, "private to start with");

        using var response = await ChattrAsync("""{"public": true}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["public"]!.GetValue<bool>().ShouldBeTrue();
        _writes.ShouldBe([("visibility", "2")]);
        (await AnonymousReadAsync()).ShouldBe(HttpStatusCode.OK, "the access reads visibility: the change took effect");
    }

    [Test]
    public async Task Public_False_MakesTheDatasetPrivate()
    {
        (await ChattrAsync("""{"public": true}""")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AnonymousReadAsync()).ShouldBe(HttpStatusCode.OK);

        using var response = await ChattrAsync("""{"public": false}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["public"]!.GetValue<bool>().ShouldBeFalse();
        _writes.Last().ShouldBe(("visibility", "0"));
        (await AnonymousReadAsync()).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task NeverWritesTheLegacyPublicKey()
    {
        await ChattrAsync("""{"public": true}""");
        await ChattrAsync("""{"public": false}""");

        _writes.Select(w => w.Key).ShouldAllBe(k => k == "visibility");
    }

    // Without "public" there is nothing to change: it is not "public: false" (which would hide the dataset)
    [TestCase("""{"isPublic": true}""")]
    [TestCase("{}")]
    [TestCase("")]
    public async Task WithoutPublic_ChangesNothing(string attributes)
    {
        (await ChattrAsync("""{"public": true}""")).StatusCode.ShouldBe(HttpStatusCode.OK);
        _writes.Clear();

        using var response = await ChattrAsync(attributes);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _writes.ShouldBeEmpty();
        (await AnonymousReadAsync()).ShouldBe(HttpStatusCode.OK, "still public");
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["public"]!.GetValue<bool>().ShouldBeTrue("reports the state");
    }

    [Test]
    public async Task AUserWithoutWriteAccess_IsRefused_AndChangesNothing()
    {
        using var response = await ChattrAsync("""{"public": true}""", OtherUser);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _writes.ShouldBeEmpty();
    }

    [Test]
    public async Task Anonymous_IsRefused()
    {
        using var response = await ChattrAsync("""{"public": true}""", user: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _writes.ShouldBeEmpty();
    }
}
