#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-12 item 9: the STAC landing page and collections listing are cached per caller, but a change of a
/// dataset cleared only the dataset's own STAC entries. A dataset made private stayed in the anonymous listings
/// until they expired (5 minutes), and through the generic meta API it was not even cleared. Now every listing
/// is keyed by the STAC cache generation, which any change that can change what a caller may list renews.
/// The cases were red before the change (the second anonymous listing still showed the dataset).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq12StacListingInvalidationTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class Rq12StacListingInvalidationTests : IDisposable
{
    private const string OwnerUser = "rq12-stac-owner";
    private const string Org = "rq12-stac-org";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    // The visibility each dataset's index answers, by dataset folder (a dataset is public until made private)
    private readonly ConcurrentDictionary<string, int> _visibility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _datasets = new(StringComparer.Ordinal);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string ddbPath, string key, string? _) => key == "visibility"
                ? $$"""{"data":{{_visibility.GetOrAdd(ddbPath, (int)Visibility.Public)}},"id":"v","mtime":0}"""
                : throw new DdbException("Key not found"));
        _factory.DdbWrapper.Setup(d => d.MetaSet(It.IsAny<string>(), "visibility", It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string ddbPath, string _, string data, string? _) =>
            {
                _visibility[ddbPath] = int.Parse(data.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);
                return new Meta { Id = "v", Data = data, ModifiedTime = DateTime.UnixEpoch };
            });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: true);
        foreach (var slug in new[] { "by-update", "by-meta", "in-collections", "renamed" })
            _datasets[slug] = Seed.Dataset(context, org, slug).InternalRef;
        await context.SaveChangesAsync();
    }

    [Test]
    public async Task MadePrivateByTheDatasetUpdate_LeavesTheAnonymousLandingPage()
    {
        (await AnonymousLandingPage()).ShouldContain(CollectionHref("by-update"));

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/orgs/{Org}/ds/by-update")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("visibility", "0")])
        };
        (await _client.SendAsync(update.AsUser(OwnerUser))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await AnonymousLandingPage()).ShouldNotContain(CollectionHref("by-update"));
    }

    [Test]
    public async Task MadePrivateThroughTheMetaApi_LeavesTheAnonymousLandingPage()
    {
        (await AnonymousLandingPage()).ShouldContain(CollectionHref("by-meta"));

        using var set = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/by-meta/meta/set/visibility")
        {
            Content = new StringContent("\"0\"", Encoding.UTF8, "application/json")
        };
        (await _client.SendAsync(set.AsUser(OwnerUser))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AnonymousLandingPage()).ShouldNotContain(CollectionHref("by-meta"));
    }

    [Test]
    public async Task MadePrivate_LeavesTheAnonymousCollectionsListing()
    {
        (await AnonymousCollectionIds()).ShouldContain($"{Org}/in-collections");

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/orgs/{Org}/ds/in-collections")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("visibility", "0")])
        };
        (await _client.SendAsync(update.AsUser(OwnerUser))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await AnonymousCollectionIds()).ShouldNotContain($"{Org}/in-collections");
    }

    /// <summary>A renamed dataset: the listings linked to its old slug (a 404) until they expired.</summary>
    [Test]
    public async Task Renamed_TheAnonymousLandingPageLinksTheNewSlug()
    {
        (await AnonymousLandingPage()).ShouldContain(CollectionHref("renamed"));

        using var rename = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/renamed/rename")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("slug", "renamed-new")])
        };
        await _client.SendAsync(rename.AsUser(OwnerUser));

        var links = await AnonymousLandingPage();
        links.ShouldNotContain(CollectionHref("renamed"));
        links.ShouldContain(CollectionHref("renamed-new"));
    }

    private static string CollectionHref(string dsSlug) => $"http://localhost/stac/collections/{Org}/{dsSlug}";

    private async Task<string[]> AnonymousLandingPage()
    {
        var response = await _client.GetAsync("/stac");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return [.. body["links"]!.AsArray().Select(l => (string)l!["href"]!)];
    }

    private async Task<string[]> AnonymousCollectionIds()
    {
        var response = await _client.GetAsync("/stac/collections?limit=100");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return [.. body["collections"]!.AsArray().Select(c => (string)c!["id"]!)];
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }
}
