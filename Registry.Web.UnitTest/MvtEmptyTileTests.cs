using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports.DroneDB;
using Registry.Web.Controllers;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// GDAL writes no file for a tile of an MVT pyramid without features. Such a tile is empty, not missing: it answers
/// 204 No Content (an OpenLayers vector tile source draws nothing there and logs no error), with the cache rule of
/// the other tiles. A file without a pyramid, or a tile off its zoom levels or grid, stays 404.
/// </summary>
[TestFixture]
public class MvtEmptyTileTests
{
    private const string Hash = "abcdef0123456789";

    private string _pyramid = null!;
    private string _metadata = null!;

    [SetUp]
    public void SetUp()
    {
        _pyramid = Path.Combine(Path.GetTempPath(), "mvt-pyramid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pyramid, "3", "4"));
        File.WriteAllBytes(Path.Combine(_pyramid, "3", "4", "5.pbf"), [1, 2, 3]);
        _metadata = Path.Combine(_pyramid, "metadata.json");
        File.WriteAllText(_metadata, """{ "name": "mvt", "minzoom": 2, "maxzoom": 6, "format": "pbf" }""");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_pyramid, true);

    // The resolver of a build whose pyramid is _pyramid, or of a file without one
    private Mock<IBuildArtifactResolver> Artifacts(bool withPyramid = true)
    {
        var artifacts = new Mock<IBuildArtifactResolver>();
        artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), Hash, ArtifactRoles.VectorMvt, It.IsAny<string?>(),
                It.IsAny<string?>()))
            .Returns((IDDB _, string _, string _, string? subPath, string? _) =>
            {
                if (!withPyramid) return null;
                var path = subPath == null ? _metadata : Path.Combine(_pyramid, subPath);
                return File.Exists(path) ? path : null;
            });
        return artifacts;
    }

    private static MvtController Controller(IBuildArtifactResolver artifacts)
    {
        var dataset = new Dataset { Slug = "ds", InternalRef = Guid.NewGuid(), Organization = new Organization { Slug = "org" } };
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync("org", "ds", AccessType.Read, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatasetAccess(dataset, () => Mock.Of<IDDB>()));
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.IsReadableAnonymously(dataset)).ReturnsAsync(true);
        return new MvtController(access.Object, artifacts, auth.Object, new FileSystem())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Test]
    public async Task ATileOfThePyramidWithoutFeatures_IsNoContent_CachedLikeTheOthers()
    {
        var controller = Controller(Artifacts().Object);

        (await controller.GetTile("org", "ds", Hash, 3, 0, 0, CancellationToken.None)).ShouldBeOfType<NoContentResult>();
        controller.Response.Headers.CacheControl.ToString().ShouldBe("public, max-age=31536000, immutable");
        controller.Response.Headers.ContentEncoding.ToString().ShouldBeEmpty();
    }

    [Test]
    public async Task ATileWithFeatures_IsTheFile()
    {
        var controller = Controller(Artifacts().Object);

        (await controller.GetTile("org", "ds", Hash, 3, 4, 5, CancellationToken.None)).ShouldBeOfType<PhysicalFileResult>();
        controller.Response.Headers.ContentEncoding.ToString().ShouldBe("gzip");
    }

    [TestCase(1, 0, 0, TestName = "AboveTheFirstZoomLevel")]
    [TestCase(7, 0, 0, TestName = "BelowTheLastZoomLevel")]
    [TestCase(3, 8, 0, TestName = "AColumnOffTheGrid")]
    [TestCase(3, 0, 8, TestName = "ARowOffTheGrid")]
    public async Task ATileOffThePyramid_IsNotFound(int z, int x, int y)
    {
        var controller = Controller(Artifacts().Object);

        (await controller.GetTile("org", "ds", Hash, z, x, y, CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        controller.Response.Headers.CacheControl.ToString().ShouldBeEmpty();
    }

    [Test]
    public async Task ATileOfAFileWithoutAPyramid_IsNotFound()
    {
        var controller = Controller(Artifacts(withPyramid: false).Object);

        (await controller.GetTile("org", "ds", Hash, 3, 0, 0, CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
    }

    [Test]
    public void TheZoomLevelsOfTheDescriptor_MayBeNumbersOrStrings_AndAnUnreadableOneCoversNothing()
    {
        const string strings = """{ "minzoom": "0", "maxzoom": "14" }""";
        MvtTiles.Covers(strings, 14, 16383, 0).ShouldBeTrue();
        MvtTiles.Covers(strings, 15, 0, 0).ShouldBeFalse();

        MvtTiles.Covers("{ not json", 3, 0, 0).ShouldBeFalse();
        MvtTiles.Covers("""{ "minzoom": 0 }""", 3, 0, 0).ShouldBeFalse();
        MvtTiles.Covers("[]", 3, 0, 0).ShouldBeFalse();
        MvtTiles.Covers(null, 3, 0, 0).ShouldBeFalse();
    }

    [Test]
    public async Task ATileOfAPyramidWhoseDescriptorWentAway_IsNotFound()
    {
        // Found, then removed by a rebuild before it is read
        var artifacts = Artifacts();
        artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), Hash, ArtifactRoles.VectorMvt, null, It.IsAny<string?>()))
            .Returns(Path.Combine(_pyramid, "gone.json"));
        var controller = Controller(artifacts.Object);

        (await controller.GetTile("org", "ds", Hash, 3, 0, 0, CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
    }

    // Through the whole pipeline: no body, no problem details, no error a browser would log
    [Test]
    public async Task AnEmptyTile_IsAnswered204WithoutABody_OverHttp()
    {
        using var factory = new RegistryWebFactory(configureServices: services =>
        {
            services.RemoveAll<IBuildArtifactResolver>();
            services.AddSingleton(Artifacts().Object);
        });
        factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "visibility", It.IsAny<string?>()))
            .Returns(() => $$"""{"data":{{(int)Visibility.Public}},"id":"visibility","mtime":0}""");
        var owner = await factory.CreateUserAsync("mvt-owner");
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            var org = Seed.Organization(context, "mvt-org", ownerId: owner.Id);
            Seed.Dataset(context, org, "mvt-ds");
            await context.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var empty = await client.GetAsync($"/orgs/mvt-org/ds/mvt-ds/mvt/{Hash}/3/0/0.pbf");
        empty.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await empty.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
        empty.Headers.CacheControl.ShouldNotBeNull().Public.ShouldBeTrue();

        using var missing = await client.GetAsync($"/orgs/mvt-org/ds/mvt-ds/mvt/{Hash}/9/0/0.pbf");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
