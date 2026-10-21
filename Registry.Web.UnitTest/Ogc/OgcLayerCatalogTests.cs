using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test.Ogc;

[TestFixture]
public class OgcLayerCatalogTests
{
    private Mock<IDdbManager> _ddbManager = null!;
    private Mock<IUtils> _utils = null!;
    private Mock<IDDB> _ddb = null!;
    private Mock<IBuildArtifactResolver> _artifacts = null!;
    private Mock<IDdbWrapper> _wrapper = null!;
    private Mock<IOgcCacheKeys> _keys = null!;
    private IDistributedCache _cache = null!;
    private OgcLayerCatalog _catalog = null!;

    private const string Org = "myorg";
    private const string Ds = "myds";

    [SetUp]
    public void Setup()
    {
        _ddbManager = new Mock<IDdbManager>();
        _utils = new Mock<IUtils>();
        _ddb = new Mock<IDDB>();
        _artifacts = new Mock<IBuildArtifactResolver>();
        _wrapper = new Mock<IDdbWrapper>();
        _keys = new Mock<IOgcCacheKeys>();

        _cache = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

        var dataset = new Dataset { InternalRef = Guid.NewGuid() };
        _utils.Setup(u => u.GetDatasetAsync(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(dataset);
        _ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(_ddb.Object);

        _catalog = new OgcLayerCatalog(
            _ddbManager.Object,
            _utils.Object,
            _cache,
            _artifacts.Object,
            _wrapper.Object,
            new OgcCacheKeys(_cache),
            NullLogger<OgcLayerCatalog>.Instance);
    }

    private static Entry VectorEntry(string path = "layer.geojson", string hash = "h1") => new()
    {
        Path = path,
        Hash = hash,
        Type = EntryType.Vector
    };

    [Test]
    public async Task GetLayersAsync_MultiLayerGpkg_ExpandsToOneDtoPerInnerLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);

        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists("/tmp/source.gpkg")).Returns(true);
        _wrapper.Setup(w => w.DescribeVector("/tmp/source.gpkg", null)).Returns(
            "{\"layers\":[" +
            "{\"name\":\"roads\",\"geometryType\":\"LineString\",\"extent\":[1.0,2.0,3.0,4.0]}," +
            "{\"name\":\"poi\",\"geometryType\":\"Point\",\"extent\":[5.0,6.0,7.0,8.0]}" +
            "]}");

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("layer.geojson:roads");
        result[0].InnerLayerName.ShouldBe("roads");
        result[0].GeometryType.ShouldBe("LineString");
        result[0].BboxWgs84.ShouldBe([1.0, 2.0, 3.0, 4.0]);
        result[1].Name.ShouldBe("layer.geojson:poi");
        result[1].InnerLayerName.ShouldBe("poi");
        result[1].BboxWgs84.ShouldBe([5.0, 6.0, 7.0, 8.0]);
    }

    // ---- DDB-12: local CAD drawings (no CRS) ----

    [Test]
    public async Task LocalLayer_HiddenFromListingsButResolvableForFeatures()
    {
        var entry = VectorEntry("site.dwg");
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists("/tmp/source.gpkg")).Returns(true);
        _wrapper.Setup(w => w.DescribeVector("/tmp/source.gpkg", null)).Returns(
            "{\"layers\":[{\"name\":\"entities\",\"geometryType\":\"LineString\",\"srs\":\"\"," +
            "\"extent\":[514000.0,5034000.0,514500.0,5034500.0]}]}");

        (await _catalog.GetLayersAsync(Org, Ds)).ShouldBeEmpty();
        (await _catalog.ResolveAsync(Org, Ds, "site.dwg:entities")).ShouldBeNull();

        var all = await _catalog.GetLayersAsync(Org, Ds, includeLocal: true);
        all.Count.ShouldBe(1);
        all[0].IsLocal.ShouldBeTrue();
        all[0].BboxWgs84.ShouldBeNull(); // drawing units are not WGS84
        all[0].SupportedCrs.ShouldBeEmpty();
        (await _catalog.ResolveAsync(Org, Ds, "site.dwg:entities", includeLocal: true)).ShouldNotBeNull();
    }

    [Test]
    public async Task LocalEntryWithoutArtifact_FlaggedFromIndexProperties()
    {
        var entry = VectorEntry("site.dwg");
        entry.Properties = new Dictionary<string, object> { ["georeferenced"] = false };
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(false);

        (await _catalog.GetLayersAsync(Org, Ds)).ShouldBeEmpty();
        (await _catalog.GetLayersAsync(Org, Ds, includeLocal: true)).Single().IsLocal.ShouldBeTrue();
    }

    [Test]
    public async Task GeoreferencedLayerWithSrs_StaysListed()
    {
        var entry = VectorEntry("site.dwg");
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists("/tmp/source.gpkg")).Returns(true);
        _wrapper.Setup(w => w.DescribeVector("/tmp/source.gpkg", null)).Returns(
            "{\"layers\":[{\"name\":\"entities\",\"srs\":\"EPSG:4326\",\"extent\":[9.1,45.4,9.2,45.5]}]}");

        var layers = await _catalog.GetLayersAsync(Org, Ds);
        layers.Single().IsLocal.ShouldBeFalse();
        layers.Single().BboxWgs84.ShouldBe([9.1, 45.4, 9.2, 45.5]);
    }

    [Test]
    public async Task GetLayersAsync_VectorArtifactMissing_FallsBackToSingleEntryLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(false);

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(entry.Path);
        result[0].InnerLayerName.ShouldBeNull();
        result[0].HasBuiltArtifact.ShouldBeFalse();
        _wrapper.Verify(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetLayersAsync_DescribeVectorThrows_FallsBackToSingleEntryLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);
        _wrapper.Setup(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("native failure"));

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(entry.Path);
        result[0].InnerLayerName.ShouldBeNull();
        result[0].HasBuiltArtifact.ShouldBeFalse();
    }

    [Test]
    public async Task GetLayersAsync_DescribeVectorReturnsEmptyLayers_FallsBackToSingleEntryLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);
        _wrapper.Setup(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("{\"layers\":[]}");

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(entry.Path);
        result[0].HasBuiltArtifact.ShouldBeFalse();
    }

    [Test]
    public async Task GetLayersAsync_AsksDroneDbForRastersAndVectorsOnly()
    {
        var raster = new Entry { Path = "ortho.tif", Hash = "hr", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([raster]);

        var result = await _catalog.GetLayersAsync(Org, Ds);

        _ddb.Verify(d => d.Find(It.Is<EntryQuery>(q => string.IsNullOrEmpty(q.Path) && q.Recursive &&
                                                        q.NamePattern == null && q.Types != null &&
                                                        q.Types.Count == 2 &&
                                                        q.Types.Contains(EntryType.GeoRaster) &&
                                                        q.Types.Contains(EntryType.Vector))), Times.Once);
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("ortho.tif");
        result[0].EntryType.ShouldBe(EntryType.GeoRaster);
    }

    [Test]
    public async Task InvalidateAsync_MakesCachedLayersAndKeysStale()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        var capsKey = await _catalog.CacheKeyAsync(Org, Ds, "caps-wms-v2-1.3.0", "folder");

        await _catalog.GetLayersAsync(Org, Ds);
        await _catalog.GetLayersAsync(Org, Ds); // cached
        _ddb.Verify(d => d.Find(It.IsAny<EntryQuery>()), Times.Once);

        await _catalog.InvalidateAsync(Org, Ds);

        await _catalog.GetLayersAsync(Org, Ds);
        _ddb.Verify(d => d.Find(It.IsAny<EntryQuery>()), Times.Exactly(2));
        (await _catalog.CacheKeyAsync(Org, Ds, "caps-wms-v2-1.3.0", "folder")).ShouldNotBe(capsKey);
    }

    [Test]
    public async Task CacheKeyAsync_IsStableUntilInvalidated_AndPerDataset()
    {
        var key = await _catalog.CacheKeyAsync(Org, Ds, "caps-wfs-v2-2.0.0", null);
        (await _catalog.CacheKeyAsync(Org, Ds, "caps-wfs-v2-2.0.0", null)).ShouldBe(key);
        (await _catalog.CacheKeyAsync(Org, Ds, "caps-wfs-v2-2.0.0", "sub")).ShouldNotBe(key);

        await _catalog.InvalidateAsync(Org, "other-ds");
        (await _catalog.CacheKeyAsync(Org, Ds, "caps-wfs-v2-2.0.0", null)).ShouldBe(key);
    }

    [Test]
    public async Task InvalidateAsync_NoOpOnBlankSlugs()
    {
        var catalog = CatalogWith(_keys.Object);
        await catalog.InvalidateAsync("", Ds);
        await catalog.InvalidateAsync(Org, "  ");
        _keys.Verify(k => k.InvalidateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task InvalidateAsync_SwallowsCacheException()
    {
        _keys.Setup(k => k.InvalidateAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Should.NotThrowAsync(() => CatalogWith(_keys.Object).InvalidateAsync(Org, Ds));
    }

    private OgcLayerCatalog CatalogWith(IOgcCacheKeys keys) => new(_ddbManager.Object, _utils.Object, _cache,
        _artifacts.Object, _wrapper.Object, keys, NullLogger<OgcLayerCatalog>.Instance);

    [Test]
    public async Task ResolveAsync_ExactMatch_ReturnsInnerLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);
        _wrapper.Setup(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("{\"layers\":[{\"name\":\"roads\"},{\"name\":\"poi\"}]}");

        var resolved = await _catalog.ResolveAsync(Org, Ds, "layer.geojson:poi");

        resolved.ShouldNotBeNull();
        resolved!.InnerLayerName.ShouldBe("poi");
    }

    [Test]
    public async Task ResolveAsync_BareEntryPath_FallsBackToFirstInnerLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);
        _wrapper.Setup(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("{\"layers\":[{\"name\":\"roads\"},{\"name\":\"poi\"}]}");

        // Ask using bare entry path - should map to first inner layer.
        var resolved = await _catalog.ResolveAsync(Org, Ds, "layer.geojson");

        resolved.ShouldNotBeNull();
        resolved!.EntryPath.ShouldBe("layer.geojson");
        resolved.InnerLayerName.ShouldBe("roads");
    }

    [Test]
    public async Task ResolveAsync_NamespacedPrefix_StripsAndResolves()
    {
        var raster = new Entry { Path = "ortho.tif", Hash = "hr", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([raster]);

        var resolved = await _catalog.ResolveAsync(Org, Ds, "ddb:ortho.tif");

        resolved.ShouldNotBeNull();
        resolved!.Name.ShouldBe("ortho.tif");
    }

    [Test]
    public async Task ResolveAsync_UnknownLayer_ReturnsNull()
    {
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([]);
        var resolved = await _catalog.ResolveAsync(Org, Ds, "nope");
        resolved.ShouldBeNull();
    }

    [Test]
    public async Task GetLayersAsync_CachesResultsAcrossCalls()
    {
        var entry = new Entry { Path = "ortho.tif", Hash = "hr", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns([entry]);

        var first = await _catalog.GetLayersAsync(Org, Ds);
        var second = await _catalog.GetLayersAsync(Org, Ds);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        _ddb.Verify(d => d.Find(It.IsAny<EntryQuery>()), Times.Once);
    }
}
