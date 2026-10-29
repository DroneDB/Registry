using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities.Ogc;
using Shouldly;
using AccessType = Registry.Web.Services.Managers.AccessType;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// OGC API – Tiles: the tile sets of a collection advertise the tile matrix set the service publishes, and a tile
/// request for any other <c>tileMatrixSetId</c> is a 404 (OGC 20-057), instead of being served as if it were that set.
/// </summary>
[TestFixture]
public class OgcApiTilesManagerTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string BaseUrl = "http://host/orgs/org/ds/ds/ogcapi";

    private static readonly OgcLayerDto Layer = new()
    {
        Name = "ortho", Title = "ortho", EntryType = EntryType.GeoRaster, EntryPath = "ortho.tif", EntryHash = "h1"
    };

    private static readonly OgcLayerDto Unbuilt = new()
    {
        Name = "raw", Title = "raw", EntryType = EntryType.GeoRaster, EntryPath = "raw.tif", EntryHash = "h2",
        HasBuiltArtifact = false
    };

    private Mock<IOgcRasterTileSource> _tiles = null!;
    private OgcApiTilesManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid(), Organization = new Organization { Slug = Org } };
        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync(Org, Ds, AccessType.Read, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatasetAccess(dataset, () => Mock.Of<IDDB>()));
        var catalog = new Mock<IOgcLayerCatalog>();
        catalog.Setup(c => c.ResolveAsync(Org, Ds, Layer.Name, It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(Layer);
        catalog.Setup(c => c.ResolveAsync(Org, Ds, Unbuilt.Name, It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync(Unbuilt);

        _tiles = new Mock<IOgcRasterTileSource>();
        _tiles.Setup(t => t.GetTileAsync(Org, Ds, It.IsAny<IDDB>(), It.IsAny<OgcLayerDto>(), 3, 1, 2, "png",
            It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);

        var dependencies = new OgcManagerDependencies(access.Object, Mock.Of<IBuildArtifactResolver>(),
            Mock.Of<IDdbWrapper>(), catalog.Object, TestCaches.Records(), new HttpContextAccessor());
        _manager = new OgcApiTilesManager(dependencies, _tiles.Object);
    }

    [Test]
    public async Task TileSets_AdvertiseTheOneTileMatrixSet_AndItsTilesAreServed()
    {
        var sets = await _manager.GetTileSetsAsync(Org, Ds, Layer.Name, BaseUrl);

        var set = sets.Tilesets.ShouldHaveSingleItem();
        set.TileMatrixSetUri.ShouldBe(OgcApiTilesConformance.WebMercatorQuadUri);
        var link = set.Links.ShouldHaveSingleItem().Href;
        link.ShouldBe($"{BaseUrl}/collections/ortho/tiles/{OgcApiTilesConformance.WebMercatorQuad}/{{z}}/{{y}}/{{x}}");

        // The id in the advertised template is the one a tile request accepts
        var id = link.Split('/')[^4];
        (await _manager.GetTileAsync(Org, Ds, Layer.Name, id, 3, 1, 2)).ShouldBe([1, 2, 3]);
    }

    [TestCase("WorldCRS84Quad")]
    [TestCase("GoogleMapsCompatible")]
    [TestCase("webmercatorquad")]
    [TestCase("")]
    public async Task Tile_OfAnotherTileMatrixSet_Is404(string tileMatrixSetId)
    {
        var ex = await Should.ThrowAsync<OgcException>(() =>
            _manager.GetTileAsync(Org, Ds, Layer.Name, tileMatrixSetId, 3, 1, 2));

        ex.HttpStatusCode.ShouldBe(404);
        ex.Locator.ShouldBe("tileMatrixSetId");
        _tiles.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Tile_OfARasterWithoutACog_IsInvalidParameterValue()
    {
        var ex = await Should.ThrowAsync<OgcException>(() =>
            _manager.GetTileAsync(Org, Ds, Unbuilt.Name, OgcApiTilesConformance.WebMercatorQuad, 3, 1, 2));

        ex.Code.ShouldBe("InvalidParameterValue");
        _tiles.VerifyNoOtherCalls();
    }
}
