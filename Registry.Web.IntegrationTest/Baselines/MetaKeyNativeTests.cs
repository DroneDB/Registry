using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The meta key rule and the answer for a key nobody wrote, on the real libddb.
/// <see cref="MetaKeyRule"/> is Registry's copy of libddb's rule, so every key of a corpus is written through
/// <see cref="NativeDdbWrapper"/> and must be accepted or refused exactly as the rule says, with the rule's message;
/// then the endpoints answer through the whole host over a real index.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~MetaKeyNativeTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Native)]
public sealed class MetaKeyNativeTests : IDisposable
{
    private const string OwnerUser = "metakeynative-meta-owner";
    private const string Org = "metakeynative-meta-org";
    private const string Ds = "metakeynative-meta-ds";
    private const string Api = $"/orgs/{Org}/ds/{Ds}/meta";

    private static readonly NativeDdbWrapper Native = new();

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _index = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(configureServices: services =>
        {
            services.RemoveAll<IDdbWrapper>();
            services.AddSingleton<IDdbWrapper>(Native);
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();

        _index = Path.Combine(_factory.RootPath, "rule-index");
        Directory.CreateDirectory(_index);
        Native.Init(_index);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // Lowercase and uppercase ASCII, the list suffix alone and after anything, digits, separators, non-ASCII
    // letters of both cases (libddb's lowercase rule is ASCII only), a key that ends in a capital S
    private static readonly string[] KeyCorpus =
    [
        "name", "names", "tag", "tags", "s", "a", "annotations", "visibility", "entries", "x1", "x1s", "a-b", "a_bs",
        "a.b", "Name", "Names", "NAME", "tagS", "S", "tAgs", "é", "és", "É", "És", "日本", "日本s", "sS", "ss"
    ];

    [Test]
    public void TheRule_IsLibddbsRule_OnEveryKeyOfTheCorpus(
        [ValueSource(nameof(KeyCorpus))] string key,
        [Values] MetaKeyKind kind)
    {
        var violation = MetaKeyRule.Violation(key, kind);

        void Write() => _ = kind == MetaKeyKind.List
            ? Native.MetaAdd(_index, key, "\"v\"")
            : Native.MetaSet(_index, key, "\"v\"");

        if (violation == null)
            Should.NotThrow(Write, $"libddb refuses '{key}' as {kind}");
        else
            Should.Throw<DdbException>(Write).Message.ShouldBe(violation);
    }

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.AsUser(OwnerUser);

        using var response = await _client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task TheEndpoints_AnswerAClientError_ForTheKeyRule_AndForAKeyNobodyWrote()
    {
        // A set on a list key and get of the key that set never wrote were 500
        var (status, body) = await SendAsync(HttpMethod.Post, $"{Api}/set/tags", "\"survey\"");
        status.ShouldBe(400);
        JsonNode.Parse(body)!["detail"]!.GetValue<string>()
            .ShouldBe("Invalid metadata key (must be singular, for example: tag)");

        (status, body) = await SendAsync(HttpMethod.Get, $"{Api}/get/tags");
        status.ShouldBe(404);
        JsonNode.Parse(body)!["detail"]!.GetValue<string>().ShouldBe("No metadata found for key tags");

        // The list is written with add, and get returns it
        (status, _) = await SendAsync(HttpMethod.Post, $"{Api}/add/tags", "\"survey\"");
        status.ShouldBe(200);
        (status, _) = await SendAsync(HttpMethod.Post, $"{Api}/add/tags", "\"mapping\"");
        status.ShouldBe(200);

        (status, body) = await SendAsync(HttpMethod.Get, $"{Api}/get/tags");
        status.ShouldBe(200);
        var list = JsonNode.Parse(body)!.AsArray();
        list.Count.ShouldBe(2);
        list[0]!["data"]!.GetValue<string>().ShouldBe("survey");
        list[1]!["data"]!.GetValue<string>().ShouldBe("mapping");

        // A single key is written with set and read as one object; absent before, 404 too
        (status, _) = await SendAsync(HttpMethod.Get, $"{Api}/get/title");
        status.ShouldBe(404);
        (status, _) = await SendAsync(HttpMethod.Post, $"{Api}/set/title", "\"Flight 1\"");
        status.ShouldBe(200);
        (status, body) = await SendAsync(HttpMethod.Get, $"{Api}/get/title");
        status.ShouldBe(200);
        JsonNode.Parse(body)!["data"]!.GetValue<string>().ShouldBe("Flight 1");

        (status, body) = await SendAsync(HttpMethod.Post, $"{Api}/add/title", "\"Flight 2\"");
        status.ShouldBe(400);
        JsonNode.Parse(body)!["detail"]!.GetValue<string>()
            .ShouldBe("Invalid metadata key (must be plural, for example: titles)");
    }
}
