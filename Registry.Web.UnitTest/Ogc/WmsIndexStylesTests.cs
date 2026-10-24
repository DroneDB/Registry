#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// The WMS styles of a raster layer are the spectral indices libddb lists for it (<c>indexLayer</c>): GetCapabilities
/// advertises them and GetMap accepts them, from the same list. Registry has no band-count rule of its own.
/// </summary>
[TestFixture]
public class WmsIndexStylesTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private static readonly byte[] IndexPng = [0x89, 0x50, 0x4E, 0x47, 9, 9];
    private static readonly byte[] RegionPng = [0x89, 0x50, 0x4E, 0x47, 1, 1];
    private static readonly double[] View = [1000.0, 2000.0, 1512.0, 2384.0];

    // What libddb reports for each raster of the dataset (the cases of ddb.h DDBGetRasterInfo)
    private static readonly (string Name, string[] IndexLayer)[] Rasters =
    [
        ("micasense", ["NDVI", "NDRE", "NDWI", "SAVI", "EVI"]), // 5-band Float32: Blue, Green, Red, RedEdge, NIR
        ("fourband", ["NDVI", "NDRE", "NDWI", "SAVI"]),         // Float32 Green, Red, RedEdge, NIR: no blue for EVI
        ("odm", ["NDVI", "NDRE", "NDWI"]),                      // UInt16 R, G, NIR, RE + alpha: no reflectance
        ("rgb", [])
    ];

    private Mock<FakeDdbWrapper> _wrapper = null!;
    private Mock<IOgcLayerCatalog> _catalog = null!;
    private Mock<IBuildArtifactResolver> _artifacts = null!;
    private WmsManager _wms = null!;

    private static string Path(string name) => $"/data/{name}.tif";

    [SetUp]
    public void SetUp()
    {
        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(dataset, AccessType.Read)).ReturnsAsync(true);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(Mock.Of<IDDB>());

        _artifacts = new Mock<IBuildArtifactResolver>();
        _artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), It.IsAny<string>(), ArtifactRoles.RasterCog, null,
            It.IsAny<string?>())).Returns((IDDB _, string _, string _, string? _, string? entryPath) => Path(System.IO.Path.GetFileNameWithoutExtension(entryPath!)));

        var layers = Rasters.Select(r => new OgcLayerDto
        {
            Name = r.Name, Title = r.Name, EntryType = EntryType.GeoRaster, EntryPath = r.Name + ".tif",
            EntryHash = "hash-" + r.Name, BboxWgs84 = [10, 40, 11, 41]
        }).Append(new OgcLayerDto { Name = "roads", EntryType = EntryType.Vector, EntryPath = "roads.geojson" }).ToList();
        _catalog = new Mock<IOgcLayerCatalog>();
        _catalog.Setup(c => c.GetLayersAsync(Org, Ds, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(layers);
        _catalog.Setup(c => c.ResolveAsync(Org, Ds, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string name, string? _, bool _, CancellationToken _) =>
                layers.FirstOrDefault(l => l.Name == name));
        _catalog.Setup(c => c.CacheKeyAsync(Org, Ds, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("caps-key");

        _wrapper = new Mock<FakeDdbWrapper> { CallBase = true };
        foreach (var (name, indexLayer) in Rasters)
            _wrapper.Setup(w => w.GetRasterInfo(Path(name))).Returns(FakeDdbWrapper.RasterInfo(indexLayer));
        _wrapper.Setup(w => w.RenderRasterIndex(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double[]>(),
            It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>())).Returns(IndexPng);
        _wrapper.Setup(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>())).Returns(RegionPng);

        IDistributedCache store = TestCaches.MemoryStore();
        var cacheManager = new CacheManager(store, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        cacheManager.Register(MagicStrings.OgcRenderCacheSeed, CacheProviderFactories.CreatePassThroughProvider(),
            TimeSpan.FromMinutes(10));

        var records = TestCaches.Records(store);
        _wms = new WmsManager(new DatasetAccessResolver(utils.Object, auth.Object, ddbManager.Object, TestPaths.Policy),
            _artifacts.Object, _wrapper.Object, _catalog.Object, records,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, NullLogger<WmsManager>.Instance,
            cacheManager, new RasterIndexCatalog(new RasterInfoCache(_wrapper.Object, records), NullLogger<RasterIndexCatalog>.Instance));
    }

    private Task<byte[]> GetMap(string layer, string? style, double[]? bbox = null) =>
        _wms.GetMapAsync(Org, Ds, [layer], [style ?? ""], bbox ?? View, "EPSG:3857", 1024, 768, "image/png", null, true);

    private int Reads() => _wrapper.Invocations.Count(i => i.Method.Name == nameof(IDdbWrapper.GetRasterInfo));

    private int IndexRenders() => _wrapper.Invocations.Count(i => i.Method.Name == nameof(IDdbWrapper.RenderRasterIndex));

    private int RegionRenders() => _wrapper.Invocations.Count(i => i.Method.Name == nameof(IDdbWrapper.RenderRasterRegion));

    // The Style names of each raster layer of a GetCapabilities document
    private async Task<Dictionary<string, string[]>> AdvertisedStyles(string version = "1.3.0")
    {
        var document = XDocument.Parse(await _wms.GetCapabilitiesAsync(Org, Ds, version));
        return document.Descendants().Where(e => e.Name.LocalName == "Layer" && e.Elements().Any(c => c.Name.LocalName == "Name"))
            .ToDictionary(l => l.Elements().First(c => c.Name.LocalName == "Name").Value,
                l => l.Elements().Where(c => c.Name.LocalName == "Style")
                    .Select(s => s.Elements().First(c => c.Name.LocalName == "Name").Value).ToArray());
    }

    [Test]
    public async Task Capabilities_AdvertiseEachLayersStylesFromLibddbsList()
    {
        var styles = await AdvertisedStyles();

        styles["micasense"].ShouldBe(["default", "NDVI", "NDRE", "NDWI", "SAVI", "EVI"]);
        styles["fourband"].ShouldBe(["default", "NDVI", "NDRE", "NDWI", "SAVI"], "no blue band: libddb lists no EVI");
        styles["odm"].ShouldBe(["default", "NDVI", "NDRE", "NDWI"], "an integer raster has no SAVI or EVI");
        styles["rgb"].ShouldBe(["default"]);
        styles.ContainsKey("roads").ShouldBeFalse("a vector layer is not a WMS layer");
    }

    [Test]
    public async Task Capabilities_Wms111_AdvertiseTheSameStyles()
    {
        (await AdvertisedStyles("1.1.1"))["fourband"].ShouldBe(["default", "NDVI", "NDRE", "NDWI", "SAVI"]);
    }

    [TestCase("micasense", "NDVI")]
    [TestCase("micasense", "ndvi")]
    [TestCase("micasense", "EVI")]
    [TestCase("fourband", "SAVI")]
    [TestCase("odm", "NDRE")]
    public async Task GetMap_AcceptsAStyleLibddbListsAndRendersItByLibddbsName(string layer, string style)
    {
        var png = await GetMap(layer, style);

        png.ShouldBe(IndexPng);
        _wrapper.Verify(w => w.RenderRasterIndex(Path(layer), style.ToUpperInvariant(), It.IsAny<double[]>(), "EPSG:3857",
            1024, 768, "image/png"), Times.Once);
        RegionRenders().ShouldBe(0);
    }

    [TestCase("fourband", "EVI")]
    [TestCase("odm", "SAVI")]
    [TestCase("odm", "EVI")]
    [TestCase("rgb", "NDVI")]
    [TestCase("rgb", "ndvi")]
    [TestCase("micasense", "thermal")]
    public async Task GetMap_RejectsAStyleLibddbDoesNotList_AsStyleNotDefined(string layer, string style)
    {
        var ex = await Should.ThrowAsync<OgcException>(() => GetMap(layer, style));

        ex.Code.ShouldBe("StyleNotDefined");
        ex.Locator.ShouldBe("STYLES");
        ex.HttpStatusCode.ShouldBe(400);
        IndexRenders().ShouldBe(0);
        RegionRenders().ShouldBe(0);
    }

    [Test]
    public async Task GetMap_EveryAdvertisedStyle_IsAccepted_AndNoOtherIsNamed()
    {
        // The contract of the layer: what Capabilities lists is what GetMap takes
        foreach (var (layer, styles) in await AdvertisedStyles())
        {
            if (layer == "rgb")
                continue;
            foreach (var style in styles.Where(s => s != "default"))
                (await GetMap(layer, style)).ShouldBe(IndexPng, $"{layer} advertises {style}");
        }
    }

    [TestCase("")]
    [TestCase("default")]
    [TestCase("DEFAULT")]
    public async Task GetMap_TheDefaultStyle_IsTheNaturalRender_AndAsksLibddbNothing(string style)
    {
        (await GetMap("rgb", style)).ShouldBe(RegionPng);

        Reads().ShouldBe(0);
        IndexRenders().ShouldBe(0);
    }

    [Test]
    public async Task GetMap_AFailedRead_IsStyleNotDefined_NotAnImage()
    {
        _wrapper.Setup(w => w.GetRasterInfo(Path("micasense"))).Throws(new DdbException("GDAL cannot open the file"));

        var ex = await Should.ThrowAsync<OgcException>(() => GetMap("micasense", "NDVI"));

        ex.Code.ShouldBe("StyleNotDefined");
        IndexRenders().ShouldBe(0);
    }

    [Test]
    public async Task TheList_IsReadOncePerRaster_ForCapabilitiesAndEveryGetMap()
    {
        await AdvertisedStyles();
        await GetMap("micasense", "NDVI");
        await GetMap("micasense", "NDRE");
        await GetMap("micasense", "NDVI", [View[0] + 100, View[1], View[2] + 100, View[3]]);
        await Should.ThrowAsync<OgcException>(() => GetMap("fourband", "EVI"));

        // Four rasters in the capabilities (the vector layer has none): one native read each, however many requests
        _wrapper.Invocations.Count(i => i.Method.Name == nameof(IDdbWrapper.GetRasterInfo)).ShouldBe(Rasters.Length);
    }
}
