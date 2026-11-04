using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Import;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="RegistryImportSource"/> browse auth handling: a rejected remote credential must
/// surface as a non-retryable 403 (<see cref="Registry.Web.Exceptions.ForbiddenException"/>), never as a raw
/// <see cref="System.InvalidOperationException"/> that the classifier collapses into a generic 500, and
/// never as a 401, which would sign the caller out of this Registry.
/// An SSRF-allowed IP literal is used as the registry host so no real DNS lookups occur.
/// </summary>
[TestFixture]
public class RegistryImportSourceTests
{
    private const string Url = "http://8.8.8.8";

    private static SsrfGuard Guard => new(new ImportSettings());

    private static RegistryImportSource SourceOf(IRemoteRegistryClient client) => new(client, Guard);

    private static JsonElement Params(string username, string password, string? org = null)
    {
        var dict = new Dictionary<string, string>
        {
            ["url"] = Url,
            ["username"] = username,
            ["password"] = password
        };
        if (org is not null)
            dict["organization"] = org;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dict));
        return doc.RootElement.Clone();
    }

    [Test]
    public async Task BrowseOrganizations_WithRejectedCredentials_ThrowsForbidden()
    {
        var client = new Mock<IRemoteRegistryClient>();
        client.Setup(c => c.AuthenticateAsync(Url, "wrong", "wrong", CancellationToken.None))
            .ReturnsAsync((string?)null);

        var ex = await Should.ThrowAsync<ForbiddenException>(
            async () => await SourceOf(client.Object).BrowseOrganizationsAsync(
                Params("wrong", "wrong"), CancellationToken.None));
        ex.Message.ShouldBe(RegistryImportSource.AuthFailureMessage);

        client.Verify(c => c.ListOrganizationsAsync(
            It.IsAny<string>(), (string?)null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task BrowseDatasets_WithRejectedCredentials_ThrowsForbidden()
    {
        var client = new Mock<IRemoteRegistryClient>();
        client.Setup(c => c.AuthenticateAsync(Url, "wrong", "wrong", CancellationToken.None))
            .ReturnsAsync((string?)null);

        var ex = await Should.ThrowAsync<ForbiddenException>(
            async () => await SourceOf(client.Object).BrowseDatasetsAsync(
                Params("wrong", "wrong", "some-org"), CancellationToken.None));
        ex.Message.ShouldBe(RegistryImportSource.AuthFailureMessage);

        client.Verify(c => c.ListDatasetsAsync(
            It.IsAny<string>(), (string?)null, "some-org", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task BrowseOrganizations_Anonymous_UsesNullTokenAndLists()
    {
        var client = new Mock<IRemoteRegistryClient>();
        client.Setup(c => c.AuthenticateAsync(Url, "", "", CancellationToken.None))
            .ReturnsAsync((string?)null);
        client.Setup(c => c.ListOrganizationsAsync(Url, (string?)null, CancellationToken.None))
            .ReturnsAsync(new[] { new RemoteBrowseItem("acme", "Acme Corp") });

        var items = await SourceOf(client.Object).BrowseOrganizationsAsync(
            Params("", ""), CancellationToken.None);

        items.Length.ShouldBe(1);
        items[0].Slug.ShouldBe("acme");
        items[0].Name.ShouldBe("Acme Corp");
    }

    [Test]
    public void RemoteAuthFailure_ClassifiesAs403NoRetryForTheSignedInCaller()
    {
        var descriptor = ApiExceptionClassifier.Classify(
            new ForbiddenException(RegistryImportSource.AuthFailureMessage), authenticated: true);

        descriptor.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        descriptor.NoRetry.ShouldBeTrue();
        descriptor.Message.ShouldBe(RegistryImportSource.AuthFailureMessage);
    }
}

/// <summary>
/// Tests for the URL of a remote registry given as the link of a page or of the API (pasted from the
/// browser) instead of the address of the registry, and for an address that answers with a web page.
/// </summary>
[TestFixture]
public class RegistryImportSourceUrlTests
{
    [TestCase("https://hub.dronedb.app", "https://hub.dronedb.app", null, null)]
    [TestCase("https://hub.dronedb.app/", "https://hub.dronedb.app", null, null)]
    [TestCase("https://hub.dronedb.app/r/odm/brighton-beach", "https://hub.dronedb.app", "odm", "brighton-beach")]
    [TestCase("https://hub.dronedb.app/r/odm/brighton-beach/view/b2R/map?x=1#top", "https://hub.dronedb.app", "odm", "brighton-beach")]
    [TestCase("https://hub.dronedb.app/r/odm", "https://hub.dronedb.app", "odm", null)]
    [TestCase("https://hub.dronedb.app/orgs/odm/ds/brighton-beach/search", "https://hub.dronedb.app", "odm", "brighton-beach")]
    [TestCase("http://example.org:5000/registry/r/acme/site", "http://example.org:5000/registry", "acme", "site")]
    [TestCase("http://example.org:5000/registry", "http://example.org:5000/registry", null, null)]
    public void SplitRegistryUrl_TakesTheBaseAddressOutOfTheLinkOfAPage(string url, string baseUrl, string? org,
        string? ds)
    {
        var (b, o, d) = RegistryImportSource.SplitRegistryUrl(new Uri(url));

        b.ShouldBe(baseUrl);
        o.ShouldBe(org);
        d.ShouldBe(ds);
    }

    [Test]
    public async Task Probe_OfTheLinkOfADatasetPage_ListsTheDatasetOfTheLinkOnTheBaseAddress()
    {
        var client = new Mock<IRemoteRegistryClient>();
        client.Setup(c => c.ListFilesAsync("http://8.8.8.8", null, "odm", "brighton-beach", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new EntryDto { Path = "a.jpg", Size = 10, Type = EntryType.Image }]);

        var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["url"] = "http://8.8.8.8/r/odm/brighton-beach"
        });

        var probe = await new RegistryImportSource(client.Object, new SsrfGuard(new ImportSettings()))
            .ProbeAsync(parameters, CancellationToken.None);

        probe.Reachable.ShouldBeTrue();
        probe.FileCount.ShouldBe(1);
        probe.SuggestedName.ShouldBe("brighton-beach");
    }

    [Test]
    public async Task ListFiles_OfAnAddressThatAnswersWithAWebPage_IsABadRequestThatNamesTheAddress()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new HtmlHandler()));
        var client = new RemoteRegistryClient(factory.Object, new ImportSettings(), null!,
            NullLogger<RemoteRegistryClient>.Instance);

        var ex = await Should.ThrowAsync<BadRequestException>(() =>
            client.ListFilesAsync("http://8.8.8.8/somewhere", null, "odm", "brighton-beach", CancellationToken.None));

        ex.Message.ShouldBe(RemoteRegistryClient.NotARegistryMessage("http://8.8.8.8/somewhere"));
        ex.NoRetry.ShouldBeTrue();
    }

    // The web app of a site: every path is its HTML page.
    private sealed class HtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<!doctype html><html></html>", Encoding.UTF8, "text/html")
            });
    }
}
