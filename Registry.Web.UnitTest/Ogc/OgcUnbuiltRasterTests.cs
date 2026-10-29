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
    private static readonly byte[] RenderedPng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static readonly double[] View = [1000.0, 2000.0, 1512.0, 2384.0];

    private static readonly Entry Built = RasterEntry("built.tif", "hash-built");
    private static readonly Entry Raw = RasterEntry("raw.tif", "hash-raw");

    private Mock<IDdbWrapper> _wrapper = null!;
    private Mock<IBuildArtifactResolver> _artifacts = null!;
    private Mock<IOgcRasterTileSource> _tiles = null!;
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

        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([Built, Raw]);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(ddb.Object);

        // Only the first raster has been built
        _artifacts = new Mock<IBuildArtifactResolver>();
        _artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), "hash-built", ArtifactRoles.RasterCog, null,
            It.IsAny<string?>())).Returns(BuiltCog);

        _wrapper = new Mock<IDdbWrapper>();
        _wrapper.Setup(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>())).Returns(RenderedPng);

        _tiles = new Mock<IOgcRasterTileSource>();
        _tiles.Setup(t => t.GetTileAsync(Org, Ds, It.IsAny<IDDB>(), It.IsAny<OgcLayerDto>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(RenderedPng);

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
    }

    private WmsManager CreateWms()
    {
        var indexes = new Mock<IRasterIndexCatalog>();
        indexes.Setup(i => i.GetAsync(It.IsAny<string>(), It.IsAny<RasterSource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return new WmsManager(_dependencies, NullLogger<WmsManager>.Instance, _cacheManager, indexes.Object);
    }

    private WmtsManager CreateWmts() => new(_dependencies, NullLogger<WmtsManager>.Instance, _tiles.Object);

    private WcsCoverageService CreateWcs() => new(_dependencies, new RasterInfoCache(_wrapper.Object, TestCaches.Records()),
        NullLogger<WcsCoverageService>.Instance);

    private static IEnumerable<string> Values(string xml, string localName) =>
        XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == localName).Select(e => e.Value);

    private static Task<byte[]> Tile(WmtsManager wmts, string layer) => wmts.GetTileAsync(Org, Ds, layer,
        WmtsConformance.DefaultStyle, WmtsConformance.DefaultTileMatrixSet, 3, 1, 2, "image/png");

    private Task<byte[]> GetMap(string layer) =>
        CreateWms().GetMapAsync(Org, Ds, [layer], [""], View, "EPSG:3857", 1024, 768, "image/png", null, true);

    private void NothingReadsTheUploadedFile()
    {
        _wrapper.Invocations.Where(i => i.Arguments.OfType<string>().Any(a => a.Contains("raw.tif"))).ShouldBeEmpty();
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

        layers.Select(l => l.EntryPath).ShouldBe(["built.tif"]);
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
        var layers = await _dependencies.LayerCatalog.GetLayersAsync(Org, Ds);
        layers.Single(l => l.EntryPath == "built.tif").HasBuiltArtifact.ShouldBeTrue();
        _artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), "hash-built", ArtifactRoles.RasterCog, null,
            It.IsAny<string?>())).Returns((string?)null);

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
        _tiles.Verify(t => t.GetTileAsync(Org, Ds, It.IsAny<IDDB>(),
            It.Is<OgcLayerDto>(l => l.EntryPath == "raw.tif"), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
