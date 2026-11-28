using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
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

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The root entry of a dataset (<c>GET /orgs/{org}/ds/{ds}</c>) says whether the dataset has a cover (a root file named like a
/// thumbnail candidate): the Hub asks for the dataset thumbnail only then, so a dataset without one logs no 404 in the
/// browser console (FR-XC-10, FR-EXP-28). The rule is the thumbnail endpoint's (an entry at a candidate path, the exact
/// name), read by a lookup per candidate, not by a listing of the root.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetInfoThumbnailHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DatasetInfoThumbnailHttpTests : IDisposable
{
    private const string OwnerUser = "datasetinfothumb-owner";
    private const string Org = "datasetinfothumb-org";
    private const string Ds = "datasetinfothumb-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
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

    private void RootFiles(params string[] names)
    {
        _factory.DdbWrapper.Reset();
        _factory.DdbWrapper.CallBase = true;
        _factory.DdbWrapper.Setup(d => d.Init(It.IsAny<string>()))
            .Returns((string directory) => System.IO.Path.Combine(directory, IDDB.DatabaseFolderName));
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new DdbException("No metadata found"));
        _factory.DdbWrapper.Setup(d => d.Info(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((string path, bool _, int _, bool _) =>
                [new Registry.Ports.DroneDB.Entry { Path = "file://" + path, Type = EntryType.DroneDB, Properties = [] }]);
        // The index: an entry at each of the names, exactly (as the index compares paths)
        _factory.DdbWrapper.Setup(d => d.GetEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string path) =>
                names.Contains(path, StringComparer.Ordinal) ? new Registry.Ports.DroneDB.Entry { Path = path, Type = EntryType.Image } : null);
    }

    private async Task<JsonObject> InfoAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}");
        request.AsUser(OwnerUser);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(200, text);
        var body = JsonNode.Parse(text)!.AsArray();
        return body[0]!["properties"]!.AsObject();
    }

    [Test]
    public async Task ADatasetWithoutACoverCandidate_SaysItHasNoThumbnail()
    {
        RootFiles("photo.jpg", "notes.txt");

        (await InfoAsync())["hasThumbnail"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Test]
    public async Task ADatasetWithACoverCandidateAtItsRoot_SaysItHasOne_WithoutListingTheRoot()
    {
        RootFiles("photo.jpg", "thumbnail.jpg");

        (await InfoAsync())["hasThumbnail"]!.GetValue<bool>().ShouldBeTrue();
        _factory.DdbWrapper.Verify(d => d.ListEntrySummaries(It.IsAny<string>(), It.IsAny<EntryQuery>()), Times.Never);
    }

    // The thumbnail endpoint finds no cover at "Thumbnail.JPG" (it looks up "thumbnail.jpg"): no 404 to ask for
    [Test]
    public async Task ACandidateNameInAnotherLetterCase_IsNoCover_AsForTheThumbnailEndpoint()
    {
        RootFiles("Thumbnail.JPG", "COVER.JPG");

        (await InfoAsync())["hasThumbnail"]!.GetValue<bool>().ShouldBeFalse();
    }
}
