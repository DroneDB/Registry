using System;
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
    private Mock<ICacheKeyScanner> _scanner = null!;
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
        _scanner = new Mock<ICacheKeyScanner>();
        _scanner.Setup(s => s.RemoveByPatternAsync(It.IsAny<string>())).ReturnsAsync(0);

        _cache = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

        var dataset = new Dataset { InternalRef = Guid.NewGuid() };
        _utils.Setup(u => u.GetDataset(Org, Ds, It.IsAny<bool>(), It.IsAny<bool>())).Returns(dataset);
        _ddbManager.Setup(m => m.Get(Org, dataset.InternalRef)).Returns(_ddb.Object);

        _catalog = new OgcLayerCatalog(
            _ddbManager.Object,
            _utils.Object,
            _cache,
            _artifacts.Object,
            _wrapper.Object,
            _scanner.Object,
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
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { entry });

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
        result[0].BboxWgs84.ShouldBe(new[] { 1.0, 2.0, 3.0, 4.0 });
        result[1].Name.ShouldBe("layer.geojson:poi");
        result[1].InnerLayerName.ShouldBe("poi");
        result[1].BboxWgs84.ShouldBe(new[] { 5.0, 6.0, 7.0, 8.0 });
    }

    [Test]
    public async Task GetLayersAsync_VectorArtifactMissing_FallsBackToSingleEntryLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { entry });
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(false);

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(entry.Path);
        result[0].InnerLayerName.ShouldBeNull();
        _wrapper.Verify(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task GetLayersAsync_DescribeVectorThrows_FallsBackToSingleEntryLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { entry });
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);
        _wrapper.Setup(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("native failure"));

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(entry.Path);
        result[0].InnerLayerName.ShouldBeNull();
    }

    [Test]
    public async Task GetLayersAsync_DescribeVectorReturnsEmptyLayers_FallsBackToSingleEntryLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { entry });
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);
        _wrapper.Setup(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("{\"layers\":[]}");

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(entry.Path);
    }

    [Test]
    public async Task GetLayersAsync_SkipsNonGeoEntries()
    {
        var img = new Entry { Path = "img.jpg", Hash = "h", Type = EntryType.Image };
        var raster = new Entry { Path = "ortho.tif", Hash = "hr", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { img, raster });

        var result = await _catalog.GetLayersAsync(Org, Ds);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("ortho.tif");
        result[0].EntryType.ShouldBe(EntryType.GeoRaster);
    }

    [Test]
    public async Task InvalidateAsync_RemovesBothCapsAndLayerKeyPatterns()
    {
        await _catalog.InvalidateAsync(Org, Ds);

        _scanner.Verify(s => s.RemoveByPatternAsync($"ogc-caps-*-{Org}-{Ds}-*"), Times.Once);
        _scanner.Verify(s => s.RemoveByPatternAsync($"ogc-layers-{Org}-{Ds}-*"), Times.Once);
    }

    [Test]
    public async Task InvalidateAsync_NoOpOnBlankSlugs()
    {
        await _catalog.InvalidateAsync("", Ds);
        await _catalog.InvalidateAsync(Org, "  ");
        _scanner.Verify(s => s.RemoveByPatternAsync(It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task InvalidateAsync_SwallowsScannerException()
    {
        _scanner.Setup(s => s.RemoveByPatternAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Should.NotThrowAsync(() => _catalog.InvalidateAsync(Org, Ds));
    }

    [Test]
    public async Task ResolveAsync_ExactMatch_ReturnsInnerLayer()
    {
        var entry = VectorEntry();
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { entry });
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
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { entry });
        _artifacts.Setup(a => a.GetVectorQueryPath(_ddb.Object, entry.Hash)).Returns("/tmp/source.gpkg");
        _artifacts.Setup(a => a.ArtifactExists(It.IsAny<string>())).Returns(true);
        _wrapper.Setup(w => w.DescribeVector(It.IsAny<string>(), It.IsAny<string>()))
            .Returns("{\"layers\":[{\"name\":\"roads\"},{\"name\":\"poi\"}]}");

        // Ask using bare entry path — should map to first inner layer.
        var resolved = await _catalog.ResolveAsync(Org, Ds, "layer.geojson");

        resolved.ShouldNotBeNull();
        resolved!.EntryPath.ShouldBe("layer.geojson");
        resolved.InnerLayerName.ShouldBe("roads");
    }

    [Test]
    public async Task ResolveAsync_NamespacedPrefix_StripsAndResolves()
    {
        var raster = new Entry { Path = "ortho.tif", Hash = "hr", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { raster });

        var resolved = await _catalog.ResolveAsync(Org, Ds, "ddb:ortho.tif");

        resolved.ShouldNotBeNull();
        resolved!.Name.ShouldBe("ortho.tif");
    }

    [Test]
    public async Task ResolveAsync_UnknownLayer_ReturnsNull()
    {
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(Array.Empty<Entry>());
        var resolved = await _catalog.ResolveAsync(Org, Ds, "nope");
        resolved.ShouldBeNull();
    }

    [Test]
    public async Task GetLayersAsync_CachesResultsAcrossCalls()
    {
        var entry = new Entry { Path = "ortho.tif", Hash = "hr", Type = EntryType.GeoRaster };
        _ddb.Setup(d => d.Search(string.Empty, true)).Returns(new[] { entry });

        var first = await _catalog.GetLayersAsync(Org, Ds);
        var second = await _catalog.GetLayersAsync(Org, Ds);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        _ddb.Verify(d => d.Search(string.Empty, true), Times.Once);
    }
}
