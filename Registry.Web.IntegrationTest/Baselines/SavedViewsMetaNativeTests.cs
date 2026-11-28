using System;
using System.IO;
using System.Linq;
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
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The saved map views live in the root meta list <c>views</c> of a dataset, on the real libddb: the meta API
/// reads and removes them, while the dataset info, the dataset and the listing leave them out of
/// <c>properties.meta</c> and keep the other keys.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~SavedViewsMetaNativeTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Native)]
public sealed class SavedViewsMetaNativeTests : IDisposable
{
    private const string OwnerUser = "savedviews-owner";
    private const string Org = "savedviews-org";
    private const string Ds = "savedviews-ds";
    private const string DsApi = $"/orgs/{Org}/ds/{Ds}";
    private const string MetaApi = $"{DsApi}/meta";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(configureServices: services =>
        {
            services.RemoveAll<IDdbWrapper>();
            services.AddSingleton<IDdbWrapper>(new NativeDdbWrapper());
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
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

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.AsUser(OwnerUser);

        using var response = await _client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonNode> GetJsonAsync(string url)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, url);
        status.ShouldBe(200, body);
        return JsonNode.Parse(body)!;
    }

    private static void ShouldLeaveOutTheViews(JsonNode? properties, string because)
    {
        var meta = properties.ShouldNotBeNull(because)["meta"]!.AsObject();
        meta.ContainsKey(DatasetMeta.ViewsKey).ShouldBeFalse(because);
        meta["name"]!["data"]!.GetValue<string>().ShouldBe("Saved views site", because);
    }

    [Test]
    public async Task TheViewsAreReadAndRemovedThroughTheMetaApi_AndLeftOutOfEveryDatasetResponse()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, $"{MetaApi}/set/name", "\"Saved views site\"");
        status.ShouldBe(200, body);
        (status, body) = await SendAsync(HttpMethod.Post, $"{MetaApi}/add/{DatasetMeta.ViewsKey}", "\"First\"");
        status.ShouldBe(200, body);
        (status, body) = await SendAsync(HttpMethod.Post, $"{MetaApi}/add/{DatasetMeta.ViewsKey}", "\"Second\"");
        status.ShouldBe(200, body);

        // The meta API returns both views
        var views = (await GetJsonAsync($"{MetaApi}/get/{DatasetMeta.ViewsKey}")).AsArray();
        views.Select(v => v!["data"]!.GetValue<string>()).ToArray().ShouldBe(["First", "Second"]);

        // The dataset info, the dataset and the listing keep the name and leave the views out
        ShouldLeaveOutTheViews((await GetJsonAsync(DsApi)).AsArray()[0]!["properties"], "dataset info");
        ShouldLeaveOutTheViews((await GetJsonAsync($"{DsApi}/ex"))["properties"], "dataset");
        ShouldLeaveOutTheViews(
            (await GetJsonAsync($"/orgs/{Org}/ds")).AsArray().Single(d => d!["slug"]!.GetValue<string>() == Ds)!["properties"],
            "listing");
        ShouldLeaveOutTheViews(
            (await GetJsonAsync($"/orgs/{Org}/ds?q={Ds}")).AsArray().Single(d => d!["slug"]!.GetValue<string>() == Ds)!["properties"],
            "paged listing");

        // Removing one view through the meta API leaves the other
        var id = views[0]!["id"]!.GetValue<string>();
        (status, body) = await SendAsync(HttpMethod.Delete, $"{MetaApi}/remove/{id}");
        status.ShouldBe(200, body);
        (await GetJsonAsync($"{MetaApi}/get/{DatasetMeta.ViewsKey}")).AsArray().Count.ShouldBe(1);
    }
}
