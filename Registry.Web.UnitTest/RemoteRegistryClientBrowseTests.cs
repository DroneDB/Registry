using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Import;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// What the Import dialog lists and suggests of a remote registry (FR-UPL-14): the display name of a dataset is in its
/// meta (<c>properties.meta.name.data</c>), not in a <c>name</c> field of the listing, and the dataset info gives the name
/// and the tagline the import keeps.
/// </summary>
[TestFixture]
public class RemoteRegistryClientBrowseTests
{
    private const string Registry = "http://remote.test";

    private const string DatasetListing = """
        [
          { "slug": "brighton-beach", "creationDate": "2024-01-01T00:00:00Z",
            "properties": { "entries": 3, "meta": { "name": { "data": "Brighton Beach" }, "tagline": { "data": "Low tide" } } } },
          { "slug": "no-meta", "creationDate": "2024-01-01T00:00:00Z", "properties": { "entries": 1 } },
          { "slug": "named", "name": "Own name", "properties": { "meta": { "name": { "data": "Ignored" } } } }
        ]
        """;

    private static RemoteRegistryClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new Handler(answer), false));
        return new RemoteRegistryClient(factory.Object, new ImportSettings(), TestPaths.Policy, TestScratch.Provider,
            NullLogger<RemoteRegistryClient>.Instance);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Test]
    public async Task ListDatasets_GivesTheMetaNameOfADataset_WhenTheListingHasNoNameField()
    {
        var client = ClientFor(_ => Json(DatasetListing));

        var items = await client.ListDatasetsAsync(Registry, null, "odm", CancellationToken.None);

        items.Select(i => (i.Slug, i.Name)).ShouldBe(
        [
            ("brighton-beach", "Brighton Beach"),
            ("no-meta", ""),
            ("named", "Own name")
        ]);
    }

    [Test]
    public async Task GetDatasetInfo_GivesTheNameAndTheTagline_FromTheDatasetUrl()
    {
        var asked = new List<string>();
        var client = ClientFor(r =>
        {
            asked.Add(r.RequestUri!.AbsolutePath);
            return Json("""{ "slug": "aukerman", "properties": { "meta": { "name": { "data": " Aukerman " }, "tagline": { "data": "Open pit" } } } }""");
        });

        var info = await client.GetDatasetInfoAsync(Registry, null, "odm", "aukerman", CancellationToken.None);

        asked.ShouldBe(["/orgs/odm/ds/aukerman"]);
        info.ShouldBe(new RemoteDatasetInfo("Aukerman", "Open pit"));
    }

    [Test]
    public async Task GetDatasetInfo_IsNull_WhenTheDatasetHasNeitherAName_NorATagline()
    {
        var client = ClientFor(_ => Json("""{ "slug": "plain", "properties": { "entries": 2 } }"""));

        (await client.GetDatasetInfoAsync(Registry, null, "odm", "plain", CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task GetDatasetInfo_IsNull_WhenTheRemoteRefusesOrAnswersWithAPage()
    {
        var refused = ClientFor(_ => Json("{}", HttpStatusCode.Forbidden));
        var page = ClientFor(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html></html>", Encoding.UTF8, "text/html")
        });

        (await refused.GetDatasetInfoAsync(Registry, null, "odm", "x", CancellationToken.None)).ShouldBeNull();
        (await page.GetDatasetInfoAsync(Registry, null, "odm", "x", CancellationToken.None)).ShouldBeNull();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }
}
