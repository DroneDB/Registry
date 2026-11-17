using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports.DroneDB;
using Registry.Web.Controllers;
using Registry.Web.Data.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// A vector tile of a build never changes, so it is cached for a year; a shared cache (a proxy, a CDN) may keep it only
/// when anyone may read its dataset: a tile of a private dataset is the caller's only. Whether anyone may read a dataset
/// is the rule of an anonymous caller, whoever asks.
/// </summary>
[TestFixture]
public class MvtCacheControlTests
{
    private const string Hash = "abcdef0123456789";

    private string _tile = null!;

    [SetUp]
    public void SetUp()
    {
        _tile = Path.Combine(Path.GetTempPath(), "mvt-tile-" + Guid.NewGuid().ToString("N") + ".pbf");
        File.WriteAllBytes(_tile, [1, 2, 3]);
    }

    [TearDown]
    public void TearDown() => File.Delete(_tile);

    private async Task<string?> CacheControlOfATile(bool readableAnonymously)
    {
        var dataset = new Dataset { Slug = "ds", InternalRef = Guid.NewGuid(), Organization = new Organization { Slug = "org" } };
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync("org", "ds", AccessType.Read, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatasetAccess(dataset, () => Mock.Of<IDDB>()));
        var artifacts = new Mock<IBuildArtifactResolver>();
        artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), Hash, ArtifactRoles.VectorMvt, MvtTiles.Tile(3, 4, 5),
                It.IsAny<string?>()))
            .Returns(_tile);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.IsReadableAnonymously(dataset)).ReturnsAsync(readableAnonymously);

        var controller = new MvtController(access.Object, artifacts.Object, auth.Object, new FileSystem())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        (await controller.GetTile("org", "ds", Hash, 3, 4, 5, CancellationToken.None)).ShouldBeOfType<PhysicalFileResult>();
        return controller.Response.Headers.CacheControl;
    }

    [Test]
    public async Task ATileOfADatasetAnyoneMayRead_IsPublic()
    {
        (await CacheControlOfATile(readableAnonymously: true)).ShouldBe("public, max-age=31536000, immutable");
    }

    [Test]
    public async Task ATileOfAPrivateDataset_IsPrivate()
    {
        (await CacheControlOfATile(readableAnonymously: false)).ShouldBe("private, max-age=31536000, immutable");
    }

    [Test]
    public async Task ADataset_IsReadableAnonymously_WhenPublicOrUnlisted_WhoeverAsks()
    {
        using var world = new DatasetVisibilityWorld();
        foreach (var caller in new[] { null, world.Member, world.Owner, world.Administrator })
        foreach (var (slug, expected) in new[] { ("a-public", true), ("b-unlisted", true), ("c-private", false) })
        {
            var dataset = world.Context.Datasets.Single(d =>
                d.InternalRef == world.Refs[DatasetVisibilityWorld.PublicOrg + "/" + slug]);
            (await world.Auth(caller).IsReadableAnonymously(dataset)).ShouldBe(expected,
                $"{slug} asked by {caller?.UserName ?? "anonymous"}");
        }
    }
}
