#nullable enable
using Registry.Web.Test.Support;
using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO.Ogc;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Registry.Web.Utilities.Ogc;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// REG-12: OGC requests authorize once, WMS GetMap renders are cached on the pixel grid of the
/// request, WMTS tiles share the Registry tile cache and WCS raster probes are cached.
/// </summary>
[TestFixture]
public class OgcRenderCachingTests
{
    private const string Org = "org";
    private const string Ds = "ds";
    private static readonly byte[] RenderedPng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    // EPSG:3857 view of 1024 x 768 px at 0.5 m/px
    private static readonly double[] View = [1000.0, 2000.0, 1512.0, 2384.0];

    private Dataset _dataset = null!;
    private Mock<IUtils> _utils = null!;
    private Mock<IAuthManager> _auth = null!;
    private Mock<IDdbWrapper> _wrapper = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IOgcLayerCatalog> _catalog = null!;
    private Mock<IOgcBuildArtifactPaths> _artifacts = null!;
    private CacheManager _cacheManager = null!;
    private MemoryDistributedCache _cache = null!;
    private DefaultHttpContext _http = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = null;
        _dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        _utils = new Mock<IUtils>();
        _utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(_dataset);
        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.RequestAccess(_dataset, AccessType.Read)).ReturnsAsync(true);

        _ddb = new Mock<IDDB>();
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, _dataset.InternalRef)).Returns(_ddb.Object);

        _artifacts = new Mock<IOgcBuildArtifactPaths>();
        _artifacts.Setup(a => a.GetCogPath(It.IsAny<IDDB>(), It.IsAny<string>())).Returns("/data/cog.tif");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);

        _catalog = new Mock<IOgcLayerCatalog>();
        _catalog.Setup(c => c.ResolveAsync(Org, Ds, "ortho", null, false)).ReturnsAsync(new OgcLayerDto
        {
            Name = "ortho", EntryType = EntryType.GeoRaster, EntryPath = "ortho.tif", EntryHash = "h1"
        });

        _wrapper = new Mock<IDdbWrapper>();
        _wrapper.Setup(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>()))
            .Returns(RenderedPng);

        _cache = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        _cacheManager = new CacheManager(_cache, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        _cacheManager.Register(MagicStrings.OgcRenderCacheSeed, CacheProviderFactories.CreatePassThroughProvider(),
            TimeSpan.FromMinutes(10));
        _cacheManager.Register(MagicStrings.TileCacheSeed, CacheProviderFactories.CreateTileProvider(),
            TimeSpan.FromMinutes(10));

        _http = new DefaultHttpContext();
        _ddbManager = ddbManager;
    }

    private Mock<IDdbManager> _ddbManager = null!;

    // One per test (a request scope): the filter and the managers of a request share its grants
    private IDatasetAccessResolver? _resolver;
    private IDatasetAccessResolver Resolver => _resolver ??= new DatasetAccessResolver(_utils.Object, _auth.Object, _ddbManager.Object, TestPaths.Policy);

    private WmsManager CreateWms() => new(Resolver, _artifacts.Object,
        _wrapper.Object, _catalog.Object, _cache, new HttpContextAccessor { HttpContext = _http },
        NullLogger<WmsManager>.Instance, _cacheManager);

    private Task<byte[]> GetMap(WmsManager wms, double[] bbox, string format = "image/png", bool transparent = true) =>
        wms.GetMapAsync(Org, Ds, ["ortho"], [""], bbox, "EPSG:3857", 1024, 768, format, null, transparent);

    private int NativeRenders() => _wrapper.Invocations.Count(i => i.Method.Name == nameof(IDdbWrapper.RenderRasterRegion));

    private static double[] Shift(double[] bbox, double dx, double dy) =>
        [bbox[0] + dx, bbox[1] + dy, bbox[2] + dx, bbox[3] + dy];

    [Test]
    public async Task GetMap_SameSnappedBbox_ServedFromCache()
    {
        var wms = CreateWms();

        var first = await GetMap(wms, View);
        var again = await GetMap(wms, View);
        var jittered = await GetMap(wms, Shift(View, 0.1, -0.1)); // 0.2 px at 0.5 m/px

        NativeRenders().ShouldBe(1);
        first.ShouldBe(RenderedPng, "a single transparent PNG layer is the native render, not re-encoded");
        again.ShouldBe(first);
        jittered.ShouldBe(first);
    }

    [Test]
    public async Task GetMap_PannedView_RendersAgain()
    {
        var wms = CreateWms();
        await GetMap(wms, View);
        await GetMap(wms, Shift(View, 10, 0)); // 20 px
        await GetMap(wms, View);

        NativeRenders().ShouldBe(2);
    }

    [Test]
    public async Task GetMap_RendersTheSnappedBbox()
    {
        var wms = CreateWms();
        await GetMap(wms, Shift(View, 0.1, 0.1));

        var rendered = (double[])_wrapper.Invocations.Single(i => i.Method.Name == nameof(IDdbWrapper.RenderRasterRegion))
            .Arguments[1];
        for (var i = 0; i < 4; i++)
            Math.Abs(rendered[i] - View[i]).ShouldBeLessThan(1e-6, "snapped to the 0.5 m grid");
    }

    [Test]
    public async Task GetMap_FailedLayer_IsNotCached()
    {
        _wrapper.SetupSequence(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("warp failed")) // native single-layer render
            .Throws(new InvalidOperationException("warp failed")) // composite retry
            .Returns(RenderedPng);
        var wms = CreateWms();

        var degraded = await GetMap(wms, View);
        degraded.ShouldNotBeEmpty("GetMap still answers with an image");
        var recovered = await GetMap(wms, View);
        var cached = await GetMap(wms, View);

        recovered.ShouldBe(RenderedPng);
        cached.ShouldBe(RenderedPng);
        NativeRenders().ShouldBe(3);
    }

    [Test]
    public async Task OgcRequest_AuthorizesOnce()
    {
        var routeData = new RouteData();
        routeData.Values["orgSlug"] = Org;
        routeData.Values["dsSlug"] = Ds;
        var filterContext = new AuthorizationFilterContext(new ActionContext(_http, routeData, new ActionDescriptor()),
            new List<IFilterMetadata>());

        await new OgcAuthorizationFilter(Resolver, NullLogger<OgcAuthorizationFilter>.Instance).OnAuthorizationAsync(filterContext);
        filterContext.Result.ShouldBeNull();
        await GetMap(CreateWms(), View);

        _auth.Verify(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()), Times.Once);
        _utils.Verify(u => u.GetDatasetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Manager_OutsideAnAuthorizedRequest_ChecksAccess()
    {
        _auth.Setup(a => a.RequestAccess(_dataset, AccessType.Read)).ReturnsAsync(false);
        // GetMap answers with its fallback image; the point is that nothing is rendered
        await GetMap(CreateWms(), View);
        _auth.Verify(a => a.RequestAccess(_dataset, AccessType.Read), Times.Once);
        NativeRenders().ShouldBe(0);
    }

    [Test]
    public async Task Wmts_GetTile_UsesSharedTileCache()
    {
        var layer = new OgcLayerDto { Name = "ortho", EntryType = EntryType.GeoRaster, EntryPath = "ortho.tif", EntryHash = "h1" };
        _ddb.Setup(d => d.GenerateTile("ortho.tif", 18, 5, 7, false, "h1", "png")).Returns(RenderedPng);
        var source = new OgcRasterTileSource(_cacheManager);

        (await source.GetTileAsync(Org, Ds, _ddb.Object, layer, 18, 5, 7, "png")).ShouldBe(RenderedPng);
        (await source.GetTileAsync(Org, Ds, _ddb.Object, layer, 18, 5, 7, "png")).ShouldBe(RenderedPng);

        _ddb.Verify(d => d.GenerateTile("ortho.tif", 18, 5, 7, false, "h1", "png"), Times.Once);
        // Stored under the tile seed and the dataset category, so dataset invalidation clears it
        (await _cache.GetAsync($":{MagicStrings.TileCacheSeed}:{Org}/{Ds}:h1-5-7-18-False-png")).ShouldBe(RenderedPng);
    }

    [Test]
    public async Task TileProvider_DoesNotReencode()
    {
        byte[] webp = [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P'];
        var provider = CacheProviderFactories.CreateTileProvider();
        (await provider(["h1", 1, 2, 3, false, new Func<Task<byte[]>>(() => Task.FromResult(webp))])).ShouldBe(webp);
    }

    [Test]
    public async Task Wcs_ProbeRaster_IsCachedPerContent()
    {
        _wrapper.Setup(w => w.GetRasterInfo("/data/cog.tif"))
            .Returns("{\"width\":2516,\"height\":1956,\"bandCount\":4,\"crs\":\"EPSG:32630\",\"bands\":[{\"name\":\"red\"}]}");
        var svc = new WcsCoverageService(Resolver, _artifacts.Object,
            _wrapper.Object, _catalog.Object, _cache, new HttpContextAccessor(), NullLogger<WcsCoverageService>.Instance);
        var layer = new OgcLayerDto { Name = "ortho", EntryType = EntryType.GeoRaster, EntryPath = "ortho.tif", EntryHash = "h1" };

        var first = await svc.ProbeRasterAsync(_ddb.Object, layer);
        var second = await svc.ProbeRasterAsync(_ddb.Object, layer);

        first.Width.ShouldBe(2516);
        second.Width.ShouldBe(2516);
        second.BandNames.ShouldBe(["red"]);
        _wrapper.Verify(w => w.GetRasterInfo(It.IsAny<string>()), Times.Once);
    }
}

/// <summary>REG-12: pure tests of the GetMap render cache key.</summary>
[TestFixture]
public class WmsRenderKeyTests
{
    private static readonly double[] View = [1000.0, 2000.0, 1512.0, 2384.0]; // 0.5 m/px at 1024 x 768
    private static readonly (string, string)[] Layers = [("h1", "")];

    private static WmsRenderKey Key(double[] bbox, int width = 1024, int height = 768, string format = "image/png",
        (string, string)[]? layers = null, string crs = "EPSG:3857") =>
        WmsRenderKey.Build(bbox, crs, width, height, format, true, null, layers ?? Layers);

    private static double[] Shift(double[] bbox, double dx, double dy) =>
        [bbox[0] + dx, bbox[1] + dy, bbox[2] + dx, bbox[3] + dy];

    [TestCase(0.0, 0.0)]
    [TestCase(0.2, -0.2)]
    [TestCase(-0.24, 0.24)]
    [TestCase(1e-9, -1e-9)]
    public void GetMap_QuantisationStableUnderJitter(double dx, double dy)
    {
        var jittered = Key(Shift(View, dx, dy));
        jittered.Key.ShouldBe(Key(View).Key);
        jittered.Bbox.ShouldBe(Key(View).Bbox);
    }

    [Test]
    public void SnappedBbox_IsWithinHalfAPixel()
    {
        var requested = Shift(View, 0.2, 0.13);
        var key = Key(requested);
        for (var i = 0; i < 4; i++)
            Math.Abs(key.Bbox[i] - requested[i]).ShouldBeLessThanOrEqualTo(0.25 + 1e-9);
    }

    [Test]
    public void DifferentRequests_HaveDifferentKeys()
    {
        var keys = new[]
        {
            Key(View).Key,
            Key(Shift(View, 1, 0)).Key,          // two pixels
            Key(View, 512, 384).Key,             // another resolution
            Key(View, format: "image/jpeg").Key,
            Key(View, layers: [("h2", "")]).Key, // rebuilt raster
            Key(View, layers: [("h1", "NDVI")]).Key,
            Key(View, crs: "EPSG:4326").Key
        };
        keys.Distinct().Count().ShouldBe(keys.Length);
    }
}
