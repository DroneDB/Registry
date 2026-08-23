#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Import;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Tests for <see cref="RegistryImportSource"/> browse auth handling: a rejected remote credential must
/// surface as a 401 (<see cref="Registry.Web.Exceptions.UnauthorizedException"/>), never as a raw
/// <see cref="System.InvalidOperationException"/> that the classifier collapses into a generic 500.
/// An SSRF-allowed IP literal is used as the registry host so no real DNS lookups occur.
/// </summary>
[TestFixture]
public class RegistryImportSourceTests
{
    private const string Url = "http://8.8.8.8";
    private const string Host = "8.8.8.8";

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
    public async Task BrowseOrganizations_WithRejectedCredentials_ThrowsUnauthorized()
    {
        var client = new Mock<IRemoteRegistryClient>();
        client.Setup(c => c.AuthenticateAsync(Url, "wrong", "wrong", CancellationToken.None))
            .ReturnsAsync((string?)null);

        var ex = await Should.ThrowAsync<UnauthorizedException>(
            async () => await SourceOf(client.Object).BrowseOrganizationsAsync(
                Params("wrong", "wrong"), CancellationToken.None));
        ex.Message.ShouldBe(RegistryImportSource.AuthFailureMessage);

        client.Verify(c => c.ListOrganizationsAsync(
            It.IsAny<string>(), (string?)null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task BrowseDatasets_WithRejectedCredentials_ThrowsUnauthorized()
    {
        var client = new Mock<IRemoteRegistryClient>();
        client.Setup(c => c.AuthenticateAsync(Url, "wrong", "wrong", CancellationToken.None))
            .ReturnsAsync((string?)null);

        var ex = await Should.ThrowAsync<UnauthorizedException>(
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
    public void UnauthorizedException_ClassifiesAs401NoRetry()
    {
        var descriptor = ApiExceptionClassifier.Classify(
            new UnauthorizedException(RegistryImportSource.AuthFailureMessage));

        descriptor.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        descriptor.NoRetry.ShouldBeTrue();
        descriptor.Message.ShouldBe(RegistryImportSource.AuthFailureMessage);
    }
}
