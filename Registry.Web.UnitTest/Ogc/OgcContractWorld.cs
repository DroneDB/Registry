using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Managers.Wcs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// A dataset with one built raster (<c>ortho.tif</c>, 12544 x 10240 pixels over the degree [10, 40, 11, 41]) behind the
/// real OGC layer catalog, managers and WCS handlers: the contract tests of the OGC services (FR-XC-31) read what GetCapabilities
/// and DescribeCoverage say and request what they offer.
/// </summary>
internal sealed class OgcContractWorld
{
    public const string Org = "org";
    public const string Ds = "ds";
    public const string Layer = "ortho.tif";
    public const int NativeWidth = 12544;
    public const int NativeHeight = 10240;
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    public static readonly byte[] Tiff = [0x49, 0x49, 0x2A, 0x00, 9, 9];

    public Mock<IDdbWrapper> Wrapper { get; } = new();
    public Mock<IDDB> Ddb { get; } = new();
    public OgcManagerDependencies Dependencies { get; }
    public RasterInfoCache RasterInfo { get; }
    public IRecordCache Records { get; }
    public DefaultHttpContext Http { get; } = new();

    /// <param name="rasterPaths">The built rasters of the dataset (<see cref="Layer"/> when none is given), each over
    /// the same degree; the index lists a folder's entries only, as the real one does.</param>
    /// <param name="vectorPaths">The built vectors of the dataset (none when not given): one inner layer each,
    /// <c>roads</c>, and the vector tile of any tile index (<see cref="Mvt"/>).</param>
    public OgcContractWorld(IReadOnlyCollection<string>? rasterPaths = null, IReadOnlyCollection<string>? vectorPaths = null)
    {
        rasterPaths = rasterPaths is { Count: > 0 } ? rasterPaths : vectorPaths is { Count: > 0 } ? [] : [Layer];
        vectorPaths ??= [];

        Http.Request.Scheme = "https";
        Http.Request.Host = new HostString("registry.test");

        var dataset = new Dataset { Slug = Ds, InternalRef = Guid.NewGuid() };
        var utils = new Mock<IDatasetResolver>();
        utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dataset);
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.RequestAccess(dataset, AccessType.Read)).ReturnsAsync(true);

        var footprint = JsonDocument.Parse(
            "{\"type\":\"Polygon\",\"coordinates\":[[[10,40],[11,40],[11,41],[10,41],[10,40]]]}").RootElement;
        var entries = rasterPaths.Select(path => new Entry
        {
            Path = path,
            Hash = HashOf(path),
            Type = EntryType.GeoRaster,
            PolygonGeometry = footprint
        }).Concat(vectorPaths.Select(path => new Entry
        {
            Path = path,
            Hash = HashOf(path),
            Type = EntryType.Vector,
            PolygonGeometry = footprint
        })).ToList();
        Ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns((EntryQuery q) => entries
            .Where(e => string.IsNullOrEmpty(q.Path) || e.Path.StartsWith(q.Path.TrimEnd('/') + "/", StringComparison.Ordinal))
            .ToList());
        Ddb.Setup(d => d.GenerateTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Png);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(Ddb.Object);

        var artifacts = new Mock<IBuildArtifactResolver>();
        foreach (var path in rasterPaths)
            artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), HashOf(path), ArtifactRoles.RasterCog, null,
                It.IsAny<string?>())).Returns(path == Layer ? "/data/ortho-cog.tif" : $"/data/{HashOf(path)}-cog.tif");

        foreach (var path in vectorPaths)
        {
            artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), HashOf(path), ArtifactRoles.VectorGpkg, null,
                It.IsAny<string?>())).Returns($"/data/{HashOf(path)}.gpkg");
            artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), HashOf(path), ArtifactRoles.VectorMvt,
                It.IsAny<string?>(), It.IsAny<string?>())).Returns(MvtFile);
            Wrapper.Setup(w => w.DescribeVector($"/data/{HashOf(path)}.gpkg")).Returns(
                "{\"layers\":[{\"name\":\"roads\",\"geometryType\":\"LineString\",\"srs\":\"EPSG:4326\"," +
                "\"extent\":[10,40,11,41]}]}");
        }

        Wrapper.Setup(w => w.GetRasterInfo(It.IsAny<string>())).Returns(
            $"{{\"width\":{NativeWidth},\"height\":{NativeHeight},\"bandCount\":3,\"crs\":\"EPSG:32615\"," +
            "\"bands\":[{\"name\":\"red\"},{\"name\":\"green\"},{\"name\":\"blue\"}]}");
        Wrapper.Setup(w => w.RenderRasterRegion(It.IsAny<string>(), It.IsAny<double[]>(), It.IsAny<string>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int[]?>(), It.IsAny<string?>())).Returns(Tiff);

        IDistributedCache store = TestCaches.MemoryStore();
        Records = TestCaches.Records(store);
        var catalog = new OgcLayerCatalog(ddbManager.Object, utils.Object, Records, artifacts.Object, Wrapper.Object,
            TestCaches.OgcKeys(store), NullLogger<OgcLayerCatalog>.Instance);
        Dependencies = new OgcManagerDependencies(
            new DatasetAccessResolver(utils.Object, auth.Object, ddbManager.Object, TestPaths.Policy),
            artifacts.Object, Wrapper.Object, catalog, Records, new HttpContextAccessor { HttpContext = Http });
        RasterInfo = new RasterInfoCache(Wrapper.Object, Records);
    }

    /// <summary>The bytes of the vector tile the world serves for a vector layer.</summary>
    public static readonly byte[] Mvt = [0x1A, 0x03, 1, 2, 3];

    private static readonly string MvtFile = WriteMvt();

    private static string WriteMvt()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ogc-contract-{Guid.NewGuid():N}.pbf");
        System.IO.File.WriteAllBytes(file, Mvt);
        return file;
    }

    private static string HashOf(string path) => path == Layer ? "hash-ortho" : $"hash-{path}";

    public WmtsManager Wmts() => new(Dependencies, NullLogger<WmtsManager>.Instance,
        new OgcRasterTileSource(NewCacheManager()));

    public WcsCoverageService WcsService(int? maxPixels = null) => new(Dependencies, RasterInfo,
        NullLogger<WcsCoverageService>.Instance, downloads: maxPixels is { } max
            ? Microsoft.Extensions.Options.Options.Create(new DownloadOptions { MaxWcsCoverageSizePixels = max })
            : null);

    public WcsManager Wcs(int? maxPixels = null)
    {
        var service = WcsService(maxPixels);
        return new WcsManager([
            new WcsProtocol10Handler(service, Records),
            new WcsProtocol11Handler(service, Records),
            new WcsProtocol20Handler(service, Records)
        ]);
    }

    private static CacheManager NewCacheManager()
    {
        IDistributedCache store = TestCaches.MemoryStore();
        var cacheManager = new CacheManager(store, NullLogger<CacheManager>.Instance,
            new NullCacheKeyScanner(NullLogger<NullCacheKeyScanner>.Instance));
        cacheManager.Register(MagicStrings.OgcRenderCacheSeed, CacheProviderFactories.CreatePassThroughProvider(),
            TimeSpan.FromMinutes(10));
        cacheManager.Register(MagicStrings.TileCacheSeed, CacheProviderFactories.CreatePassThroughProvider(),
            TimeSpan.FromMinutes(10));
        return cacheManager;
    }
}
