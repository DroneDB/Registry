#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Serilog.Core;
using Shouldly;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// End-to-end check of Registry eb1311f2 + libddb 2.0.0, finding 1: <c>GET thumb?path=model/x.obj</c> was a 500 with
/// an ERR stack (<c>nxs/model.nxz not recognized as being in a supported file format</c>): the thumbnail source of a
/// model is its Nexus artifact, which GDAL cannot open. libddb says per type whether it has a thumbnail
/// (<c>DDBGetEntryTypes</c>: <c>thumbnail: none|file|build</c>); the thumbnail path asks that one rule and answers
/// 404 (the entry has no thumbnail) before any native render, without an error log. 404 and not 415: the
/// request is fine, the thumbnail resource does not exist; the Hub loads thumbnails as images and treats any
/// error status alike, and the dataset thumbnail already answers 404 when it has no source.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ThumbnailRuleHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ThumbnailRuleHttpTests : IDisposable
{
    private const string OwnerUser = "thumb-rule-owner";
    private const string Org = "thumb-rule-org";
    private const string Ds = "thumb-rule-ds";
    private const string Hash = "d0d2b869615dca09ef0055f2014401bab298503e3e1f10e339f14f5788ea702f";
    private static readonly byte[] ThumbnailBytes = [1, 2, 3, 4];

    private readonly LogCapture _logs = new();
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _datasetFolder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(
            new System.Collections.Generic.Dictionary<string, string> { ["Serilog:LevelSwitches:$controlSwitch"] = "Information" },
            services => services.AddSingleton<ILogEventSink>(_logs));
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
        // The local generator reads the file before it renders it
        await File.WriteAllBytesAsync(Path.Combine(_datasetFolder, "photo.jpg"), [1, 2, 3]);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _logs.Clear();
        _factory.DdbWrapper.Invocations.Clear();
        _factory.DdbWrapper.Setup(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>())).Returns(ThumbnailBytes);
    }

    private void SetupEntry(string path, EntryType type) =>
        _factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, path))
            .Returns(new Entry { Path = path, Hash = Hash, Type = type, Size = 3 });

    private async Task<HttpResponseMessage> GetThumbnailAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/thumb?path={path}&size=256")
            .AsUser(OwnerUser);
        return await _client.SendAsync(request);
    }

    [TestCase("model/x.obj", EntryType.Model)]
    [TestCase("model/x.glb", EntryType.Model)]
    [TestCase("model/x.mtl", EntryType.Generic)]
    [TestCase("notes.md", EntryType.Markdown)]
    [TestCase("clip.mp4", EntryType.Video)]
    [TestCase("pano.jpg", EntryType.Panorama)]
    [TestCase("roads.geojson", EntryType.Vector)]
    public async Task EntryOfATypeWithoutAThumbnail_Is404_WithoutNativeWorkOrErrorLog(string path, EntryType type)
    {
        SetupEntry(path, type);

        using var response = await GetThumbnailAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("has no thumbnail");
        _factory.DdbWrapper.Verify(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _logs.AtLeast(LogLevel.Error).ShouldBeEmpty("a client asking for a thumbnail that does not exist is not a server fault");
    }

    [Test]
    public async Task EntryOfATypeWithAThumbnailFile_IsRendered()
    {
        SetupEntry("photo.jpg", EntryType.GeoImage);

        using var response = await GetThumbnailAsync("photo.jpg");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(ThumbnailBytes);
    }

    [Test]
    public async Task EntryWhoseThumbnailComesFromABuild_NotBuiltYet_Is404NotAvailableYet()
    {
        // thumbnail "build" (point clouds, splats): the rule lets it through, the artifact is what is missing
        SetupEntry("cloud.laz", EntryType.PointCloud);
        _factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Length == 1 && p[0] == "cloud.laz")))
            .Returns([TestBuilds.Build(_datasetFolder, "cloud.laz", EntryType.PointCloud, Hash, BuildState.None)]);

        using var response = await GetThumbnailAsync("cloud.laz");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("is not available yet");
        _factory.DdbWrapper.Verify(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }
}
