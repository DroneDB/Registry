#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-11 (XI-04 Registry side) through HTTP on <see cref="RegistryWebFactory"/>: the build endpoints read
/// libddb's build manifest instead of composing the build layout.
/// <list type="bullet">
/// <item><c>GET build/{hash}/{path}</c> of a file that is not part of the build was a 500 (the composed path
/// went to <c>PhysicalFile</c>, which threw <c>FileNotFoundException</c> while the result executed); libddb
/// resolves the file now (<c>DDBResolveBuildFile</c>), so it is a 404.</item>
/// <item><c>GET build-artifact</c> serves the manifest's artifact and download name with the media type
/// Registry has always sent (the switch to the manifest's media types is a pending decision).</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq11BuildFilesHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class Rq11BuildFilesHttpTests : IDisposable
{
    private const string OwnerUser = "rq11-owner";
    private const string Org = "rq11-org";
    private const string Ds = "rq11-ds";
    private const string Hash = "d0d2b869615dca09ef0055f2014401bab298503e3e1f10e339f14f5788ea702f";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _datasetFolder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
        _datasetFolder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void ResetDdb()
    {
        _factory.DdbWrapper.Invocations.Clear();
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string?)null);
    }

    // The build folder of the entry, where libddb (and the old composed path) has its files
    private string BuildFile(string relative, string content)
    {
        var path = Path.Combine(_datasetFolder, ".ddb", "build", Hash, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url).AsUser(OwnerUser);
        return await _client.SendAsync(request);
    }

    [Test]
    public async Task BuildFile_NotPartOfTheBuild_Is404NotA500()
    {
        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/cog/missing.tif");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task BuildFile_OfTheBuild_IsServedWithTheMediaTypeOfItsExtension()
    {
        var file = BuildFile("3dtiles/tileset.json", """{"asset":{"version":"1.0"}}""");
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(_datasetFolder, Hash, "3dtiles/tileset.json")).Returns(file);

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/3dtiles/tileset.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync()).ShouldBe("""{"asset":{"version":"1.0"}}""");
    }

    [Test]
    public async Task BuildFile_Head_AnswersWhetherTheFileIsPartOfTheBuild()
    {
        var file = BuildFile("cog/cog.tif", "tiff");
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(_datasetFolder, Hash, "cog/cog.tif")).Returns(file);

        using var present = await SendAsync(HttpMethod.Head, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/cog/cog.tif");
        using var absent = await SendAsync(HttpMethod.Head, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/cog/other.tif");

        present.StatusCode.ShouldBe(HttpStatusCode.OK);
        absent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static IEnumerable<TestCaseData> Downloads()
    {
        yield return new TestCaseData("ortho.tif", EntryType.GeoRaster, ArtifactRoles.RasterCog, "cog/cog.tif",
            "image/tiff; application=geotiff; profile=cloud-optimized", "cog.tif", "image/tiff").SetName("{m}(cog)");
        yield return new TestCaseData("cloud.laz", EntryType.PointCloud, ArtifactRoles.PointCloudCopc,
            "copc/cloud.copc.laz", "application/vnd.laszip+copc", "cloud.copc.laz", "application/octet-stream")
            .SetName("{m}(copc)");
        yield return new TestCaseData("roads.geojson", EntryType.Vector, ArtifactRoles.VectorGpkg, "vec/source.gpkg",
            "application/geopackage+sqlite3", "source.gpkg", "application/geopackage+sqlite").SetName("{m}(gpkg)");
    }

    [TestCaseSource(nameof(Downloads))]
    public async Task BuildArtifact_IsTheManifestsDownload_WithTodaysMediaType(string path, EntryType type,
        string role, string artifactPath, string manifestType, string downloadName, string servedType)
    {
        BuildFile(artifactPath, "artifact bytes");
        SetupEntry(path, type, BuildState.Complete,
            TestBuilds.File(role, artifactPath, manifestType, downloadName));

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build-artifact?path={path}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe(servedType);
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe(downloadName);
        (await response.Content.ReadAsStringAsync()).ShouldBe("artifact bytes");
    }

    [Test]
    public async Task BuildArtifact_NotBuiltYet_Is404()
    {
        SetupEntry("late.tif", EntryType.GeoRaster, BuildState.None, hash: new string('7', 64));

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build-artifact?path=late.tif");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("may not have been built yet");
    }

    [Test]
    public async Task BuildArtifact_OfAnEntryWithoutDownload_Is400()
    {
        _factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, "photo.jpg"))
            .Returns(new Entry { Path = "photo.jpg", Hash = "1234", Type = EntryType.Image, Size = 1 });
        _factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == "photo.jpg")))
            .Returns([TestBuilds.NotBuildable("photo.jpg", EntryType.Image)]);

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build-artifact?path=photo.jpg");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("does not have a downloadable build artifact");
    }

    private void SetupEntry(string path, EntryType type, BuildState state, params BuildArtifact[] artifacts) =>
        SetupEntry(path, type, state, Hash, artifacts);

    private void SetupEntry(string path, EntryType type, BuildState state, string hash, params BuildArtifact[] artifacts)
    {
        _factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, path))
            .Returns(new Entry { Path = path, Hash = hash, Type = type, Size = 1 });
        _factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == path)))
            .Returns([TestBuilds.Build(_datasetFolder, path, type, hash, state, artifacts)]);
    }
}
