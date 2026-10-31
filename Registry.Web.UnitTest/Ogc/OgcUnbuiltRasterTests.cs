using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// OGC services read the COG of the build of a raster, never the file a user uploaded: a raster without a COG is
/// left out of every GetCapabilities and a request for it is an InvalidParameterValue.
/// </summary>
[TestFixture]
public class OgcUnbuiltRasterTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private const string BuiltCog = "/data/built-cog.tif";
    private const string OtherCog = "/data/other-cog.tif";
    private const string BaseUrl = "http://host/orgs/org/ds/ds/ogcapi";
    private static readonly byte[] RenderedPng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static readonly double[] View = [1000.0, 2000.0, 1512.0, 2384.0];

    private static readonly Entry Built = RasterEntry("built.tif", "hash-built");
    private static readonly Entry Raw = RasterEntry("raw.tif", "hash-raw");
    private static readonly Entry Other = RasterEntry("other.tif", "hash-other");

    private Mock<IDdbWrapper> _wrapper = null!;
    private Mock<IBuildArtifactResolver> _artifacts = null!;
    private Mock<IDDB> _ddb = null!;
    private OgcRasterTileSource _tiles = null!;
    private OgcManagerDependencies _dependencies = null!;
    private CacheManager _cacheManager = null!;

    private static Entry RasterEntry(string path, string hash) => new()
    {
        Path = path,
        Hash = hash,
        Type = EntryType.GeoRaster,
        PolygonGeometry = JsonDocument.Parse(
            "{\"type\":\"Polygon\",\"coordinates\":[[[10,40],[11,40],[11,41],[10,41],[10,40]]]}").RootElement
    };

    [SetUp]
    public void SetUp()
    {
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(dataset, AccessType.Read)).ReturnsAsync(true);

        // Tiles are rendered by the real tile source: what it hands to libddb is what a request reads
        _ddb = new Mock<IDDB>();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([Built, Raw, Other]);
        _ddb.Setup(d => d.GenerateTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>())).Returns(RenderedPng);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(_ddb.Object);

        // The raw raster has not been built
        _artifacts = new Mock<IBuildArtifactResolver>();
        _artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), "hash-built", ArtifactRoles.RasterCog, null,
            It.IsAny<string?>())).Returns(BuiltCog);
        _artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), "hash-other", ArtifactRoles.RasterCog, null,
            It.IsAny<string?>())).Returns(OtherCog);

        _wrapper = new Mock<IDdbWrapper>();
        _wrapper.Setup(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>())).Returns(RenderedPng);

        IDistributedCache store = TestCaches.MemoryStore();
        var catalog = new OgcLayerCatalog(ddbManager.Object, utils.Object, TestCaches.Records(store), _artifacts.Object,
            _wrapper.Object, TestCaches.OgcKeys(store), NullLogger<OgcLayerCatalog>.Instance);
        _dependencies = new OgcManagerDependencies(
            new DatasetAccessResolver(utils.Object, auth.Object, ddbManager.Object, TestPaths.Policy),
            _artifacts.Object, _wrapper.Object, catalog, TestCaches.Records(store),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        _cacheManager = new CacheManager(store, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        _cacheManager.Register(MagicStrings.OgcRenderCacheSeed, CacheProviderFactories.CreatePassThroughProvider(),
            TimeSpan.FromMinutes(10));
        _cacheManager.Register(MagicStrings.TileCacheSeed, CacheProviderFactories.CreatePassThroughProvider(),
            TimeSpan.FromMinutes(10));
        _tiles = new OgcRasterTileSource(_cacheManager);
    }

    private WmsManager CreateWms()
    {
        var indexes = new Mock<IRasterIndexCatalog>();
        indexes.Setup(i => i.GetAsync(It.IsAny<string>(), It.IsAny<RasterSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return new WmsManager(_dependencies, NullLogger<WmsManager>.Instance, _cacheManager, indexes.Object);
    }

    private WmtsManager CreateWmts() => new(_dependencies, NullLogger<WmtsManager>.Instance, _tiles);

    private OgcApiTilesManager CreateOgcApiTiles() => new(_dependencies, _tiles);

    private WcsCoverageService CreateWcs() => new(_dependencies, new RasterInfoCache(_wrapper.Object, TestCaches.Records()),
        NullLogger<WcsCoverageService>.Instance);

    private static IEnumerable<string> Values(string xml, string localName) =>
        XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == localName).Select(e => e.Value);

    private static Task<byte[]> Tile(WmtsManager wmts, string layer) => wmts.GetTileAsync(Org, Ds, layer,
        WmtsConformance.DefaultStyle, WmtsConformance.DefaultTileMatrixSet, 3, 1, 2, "image/png");

    private Task<byte[]> GetMap(string layer) =>
        CreateWms().GetMapAsync(Org, Ds, [layer], [""], View, "EPSG:3857", 1024, 768, "image/png", null, true);

    private Task<byte[]?> OgcApiTile(string collectionId) => CreateOgcApiTiles().GetTileAsync(Org, Ds, collectionId,
        OgcApiTilesConformance.WebMercatorQuad, 3, 1, 2);

    // The layer list was cached while the built raster had its COG; the COG is gone since
    private async Task LoseTheCogOfTheBuiltRasterAfterTheLayerListWasCached()
    {
        var layers = await _dependencies.LayerCatalog.GetLayersAsync(Org, Ds);
        layers.Single(l => l.EntryPath == "built.tif").HasBuiltArtifact.ShouldBeTrue();
        _artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), "hash-built", ArtifactRoles.RasterCog, null,
            It.IsAny<string?>())).Returns((string?)null);
    }

    // The tiles libddb rendered and the file each one read
    private List<string> TiledFiles() => _ddb.Invocations.Where(i => i.Method.Name == nameof(IDDB.GenerateTile))
        .Select(i => i.Arguments[0] as string ?? string.Empty).ToList();

    private void NothingReadsTheUploadedFile()
    {
        _wrapper.Invocations.Where(i => i.Arguments.OfType<string>().Any(a => a.Contains("raw.tif"))).ShouldBeEmpty();
        TiledFiles().ShouldNotContain("raw.tif");
        _wrapper.Verify(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task Catalog_FlagsAnUnbuiltRaster()
    {
        var layers = await _dependencies.LayerCatalog.GetLayersAsync(Org, Ds);

        layers.Single(l => l.EntryPath == "built.tif").HasBuiltArtifact.ShouldBeTrue();
        layers.Single(l => l.EntryPath == "raw.tif").HasBuiltArtifact.ShouldBeFalse();
    }

    [Test]
    public async Task WmsCapabilities_ListOnlyTheBuiltRaster()
    {
        foreach (var version in new[] { "1.1.1", "1.3.0" })
        {
            var names = Values(await CreateWms().GetCapabilitiesAsync(Org, Ds, version), "Name").ToList();

            names.ShouldContain("built.tif");
            names.ShouldNotContain("raw.tif");
        }
    }

    [Test]
    public async Task WmtsCapabilities_ListOnlyTheBuiltRaster()
    {
        var identifiers = Values(await CreateWmts().GetCapabilitiesAsync(Org, Ds), "Identifier").ToList();

        identifiers.ShouldContain(OgcNames.ToNcName("built.tif"));
        identifiers.ShouldNotContain(OgcNames.ToNcName("raw.tif"));
    }

    [Test]
    public async Task WcsCoverages_ListOnlyTheBuiltRaster()
    {
        var (_, layers) = await CreateWcs().GetCoveragesAsync(Org, Ds, null);

        layers.Select(l => l.EntryPath).ShouldBe(["built.tif", "other.tif"]);
    }

    [Test]
    public async Task WmsGetMap_BuiltRaster_RendersTheCog()
    {
        (await GetMap("built.tif")).ShouldBe(RenderedPng);

        _wrapper.Verify(w => w.RenderRasterRegion(BuiltCog, It.IsAny<double[]>(), "EPSG:3857", 1024, 768, "image/png",
            It.IsAny<int[]?>(), It.IsAny<string?>()), Times.Once);
    }

    [Test]
    public async Task WmsGetMap_UnbuiltRaster_IsInvalidParameterValue()
    {
        var ex = await Should.ThrowAsync<OgcException>(() => GetMap("raw.tif"));

        ex.Code.ShouldBe("InvalidParameterValue");
        ex.Locator.ShouldBe("LAYERS");
        NothingReadsTheUploadedFile();
    }

    [Test]
    public async Task WmsGetMap_CogGoneSinceTheLayerListWasCached_NeverReadsTheEntryFile()
    {
        // The layer list says built; the COG is no longer there: the entry file is not a fallback, the layer
        // is drawn as the transparent layer any failed render is
        await LoseTheCogOfTheBuiltRasterAfterTheLayerListWasCached();

        await GetMap("built.tif");

        NothingReadsTheUploadedFile();
    }

    [Test]
    public async Task WmtsGetTile_UnbuiltRaster_IsInvalidParameterValue()
    {
        var wmts = CreateWmts();

        (await Tile(wmts, "built.tif")).ShouldBe(RenderedPng);

        var ex = await Should.ThrowAsync<OgcException>(() =>
            Tile(wmts, "raw.tif"));
        ex.Code.ShouldBe("InvalidParameterValue");
        ex.Locator.ShouldBe("layer");
        NothingReadsTheUploadedFile();
    }

    [Test]
    public async Task WmtsGetTile_BuiltRaster_TilesTheCog()
    {
        (await Tile(CreateWmts(), "built.tif")).ShouldBe(RenderedPng);

        TiledFiles().ShouldBe([BuiltCog]);
        _ddb.Verify(d => d.GenerateTile(BuiltCog, 3, It.IsAny<int>(), It.IsAny<int>(), false, "hash-built", "png"),
            Times.Once);
    }

    [Test]
    public async Task WmtsGetTile_CogGoneSinceTheLayerListWasCached_IsInvalidParameterValue()
    {
        await LoseTheCogOfTheBuiltRasterAfterTheLayerListWasCached();

        var ex = await Should.ThrowAsync<OgcException>(() => Tile(CreateWmts(), "built.tif"));

        ex.Code.ShouldBe("InvalidParameterValue");
        ex.Locator.ShouldBe("layer");
        TiledFiles().ShouldBeEmpty();
    }

    [Test]
    public async Task OgcApiTile_BuiltRaster_TilesTheCog()
    {
        (await OgcApiTile("built.tif")).ShouldBe(RenderedPng);

        TiledFiles().ShouldBe([BuiltCog]);
        _ddb.Verify(d => d.GenerateTile(BuiltCog, 3, 1, 2, false, "hash-built", "png"), Times.Once);
    }

    [Test]
    public async Task OgcApiTile_UnbuiltRaster_IsInvalidParameterValue()
    {
        var ex = await Should.ThrowAsync<OgcException>(() => OgcApiTile("raw.tif"));

        ex.Code.ShouldBe("InvalidParameterValue");
        ex.Locator.ShouldBe("collectionId");
        NothingReadsTheUploadedFile();
    }

    [Test]
    public async Task WmsCapabilities_CogGoneSinceTheLayerListWasCached_LeaveOutOnlyThatRaster()
    {
        await LoseTheCogOfTheBuiltRasterAfterTheLayerListWasCached();

        var names = Values(await CreateWms().GetCapabilitiesAsync(Org, Ds, "1.3.0"), "Name").ToList();

        names.ShouldContain("other.tif");
        names.ShouldNotContain("built.tif");
        names.ShouldNotContain("raw.tif");
    }

    [Test]
    public async Task WmsGetFeatureInfo_UnbuiltRaster_IsInvalidParameterValueOfQueryLayers()
    {
        var ex = await Should.ThrowAsync<OgcException>(() => CreateWms().GetFeatureInfoAsync(Org, Ds, "raw.tif", View,
            "EPSG:3857", 1024, 768, 10, 10, "application/json"));

        ex.Code.ShouldBe("InvalidParameterValue");
        ex.Locator.ShouldBe("QUERY_LAYERS");
        NothingReadsTheUploadedFile();
    }

    [Test]
    public async Task OgcApiCollections_ListOnlyTheBuiltRasters()
    {
        var collections = await new OgcApiFeaturesManager(_dependencies, NullLogger<OgcApiFeaturesManager>.Instance)
            .GetCollectionsAsync(Org, Ds, BaseUrl);

        collections.Collections.Select(c => c.Id).ShouldBe(["built.tif", "other.tif"]);
    }

    [Test]
    public async Task OgcApiCollection_BuiltRaster_IsDescribed()
    {
        var collection = await new OgcApiFeaturesManager(_dependencies, NullLogger<OgcApiFeaturesManager>.Instance)
            .GetCollectionAsync(Org, Ds, "built.tif", BaseUrl);

        collection.ShouldNotBeNull().Id.ShouldBe("built.tif");
    }

    [Test]
    public async Task OgcApiCollection_UnbuiltRaster_IsNotFound()
    {
        var collection = await new OgcApiFeaturesManager(_dependencies, NullLogger<OgcApiFeaturesManager>.Instance)
            .GetCollectionAsync(Org, Ds, "raw.tif", BaseUrl);

        collection.ShouldBeNull();
    }

    [Test]
    public async Task OgcApiTileSets_UnbuiltRaster_IsInvalidParameterValue()
    {
        var tiles = CreateOgcApiTiles();

        (await tiles.GetTileSetsAsync(Org, Ds, "built.tif", BaseUrl)).Tilesets.ShouldHaveSingleItem();

        var ex = await Should.ThrowAsync<OgcException>(() => tiles.GetTileSetsAsync(Org, Ds, "raw.tif", BaseUrl));
        ex.Code.ShouldBe("InvalidParameterValue");
        ex.Locator.ShouldBe("collectionId");
    }

    [Test]
    public async Task WcsCoverage_UnbuiltRaster_IsInvalidParameterValue()
    {
        var wcs = CreateWcs();

        (await wcs.ResolveCoverageAsync(Org, Ds, "built.tif")).Layer.EntryPath.ShouldBe("built.tif");

        var single = await Should.ThrowAsync<OgcException>(() => wcs.ResolveCoverageAsync(Org, Ds, "raw.tif"));
        single.Code.ShouldBe("InvalidParameterValue");
        var several = await Should.ThrowAsync<OgcException>(() =>
            wcs.ResolveCoveragesAsync(Org, Ds, ["built.tif", "raw.tif"]));
        several.Code.ShouldBe("InvalidParameterValue");
    }
}
