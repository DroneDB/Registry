#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Testing;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test.Ogc;

/// <summary>
/// XI-04: the spectral indices a raster can render come from libddb's <c>indexLayer</c>, read once per content,
/// source file and libddb version.
/// </summary>
[TestFixture]
public class RasterIndexCatalogTests
{
    private const string Hash = "h1";
    private static readonly RasterSource Cog = new("/data/cog.tif", ArtifactRoles.RasterCog);

    private Mock<FakeDdbWrapper> _ddb = null!;
    private string _info = null!;
    private CapturingLogger<RasterIndexCatalog> _log = null!;
    private string _version = null!;

    [SetUp]
    public void SetUp()
    {
        _store = TestCaches.MemoryStore();
        _version = "2.0.0";
        _info = FakeDdbWrapper.RasterInfo("NDVI", "NDRE", "NDWI", "SAVI", "EVI");
        _log = new CapturingLogger<RasterIndexCatalog>();
        _ddb = new Mock<FakeDdbWrapper> { CallBase = true };
        _ddb.Setup(d => d.GetVersion()).Returns(() => _version);
        _ddb.Setup(d => d.GetRasterInfo(It.IsAny<string>())).Returns(() => _info);
    }

    // One store for the catalogs of a test: what a catalog caches, another instance reads (the distributed cache)
    private IDistributedCache _store = null!;

    private RasterIndexCatalog Create() => new(_ddb.Object, TestCaches.Records(_store), _log);

    private int Reads() => _ddb.Invocations.Count(i => i.Method.Name == nameof(IDdbWrapper.GetRasterInfo));

    [Test]
    public async Task Get_ReturnsLibddbsListInItsOrder()
    {
        var list = await Create().GetAsync(Hash, Cog);

        list.ShouldBe(["NDVI", "NDRE", "NDWI", "SAVI", "EVI"]);
        _ddb.Verify(d => d.GetRasterInfo("/data/cog.tif"), Times.Once);
    }

    [Test]
    public async Task Get_ARasterWithNoIndex_IsAnEmptyListAndIsCached()
    {
        _info = FakeDdbWrapper.RasterInfo();
        var catalog = Create();

        (await catalog.GetAsync(Hash, Cog)).ShouldBeEmpty();
        (await catalog.GetAsync(Hash, Cog)).ShouldBeEmpty();

        Reads().ShouldBe(1, "[] is libddb's answer, kept like any other");
    }

    [Test]
    public async Task Get_IsReadOncePerContent_AcrossCatalogsSharingTheCache()
    {
        await Create().GetAsync(Hash, Cog);
        await Create().GetAsync(Hash, Cog);
        await Create().GetAsync(Hash, Cog);

        Reads().ShouldBe(1);
    }

    [Test]
    public async Task Get_AChangedContent_IsReadAgain()
    {
        await Create().GetAsync("hash-before-the-rebuild", Cog);

        _info = FakeDdbWrapper.RasterInfo("NDVI");
        var after = await Create().GetAsync("hash-after-the-rebuild", Cog);

        after.ShouldBe(["NDVI"], "another hash is another record, never the one of the previous content");
        Reads().ShouldBe(2);
    }

    [Test]
    public async Task Get_TheEntryFileAndItsBuildCog_AreTwoRecords()
    {
        _ddb.Setup(d => d.GetRasterInfo("/data/ortho.tif")).Returns(FakeDdbWrapper.RasterInfo("NDVI"));

        var cog = await Create().GetAsync(Hash, Cog);
        var file = await Create().GetAsync(Hash, new RasterSource("/data/ortho.tif", RasterSource.EntryFile));

        cog.Count.ShouldBe(5);
        file.ShouldBe(["NDVI"]);
    }

    [Test]
    public async Task Get_ANewLibddbVersion_ReadsAgain()
    {
        await Create().GetAsync(Hash, Cog);

        _version = "2.1.0";
        _info = FakeDdbWrapper.RasterInfo("NDVI", "NDRE");
        var list = await Create().GetAsync(Hash, Cog);

        list.ShouldBe(["NDVI", "NDRE"], "a libddb that changes the rule never reads what its predecessor kept");
    }

    [Test]
    public async Task Get_AFailedRead_IsNoIndexAndIsNotRemembered()
    {
        var fail = true;
        _ddb.Setup(d => d.GetRasterInfo(It.IsAny<string>()))
            .Returns(() => fail ? throw new DdbException("GDAL cannot open the file") : _info);
        var catalog = Create();

        (await catalog.GetAsync(Hash, Cog)).ShouldBeEmpty();
        _log.Count(LogLevel.Warning).ShouldBe(1);

        fail = false;
        (await catalog.GetAsync(Hash, Cog)).Count.ShouldBe(5, "the next request asks libddb again");
    }

    [Test]
    public async Task Get_AnInfoThatIsNotJson_IsNoIndex()
    {
        _info = "not json";

        (await Create().GetAsync(Hash, Cog)).ShouldBeEmpty();
        _log.Count(LogLevel.Warning).ShouldBe(1);
    }

    [Test]
    public async Task Get_AnUnexpectedFailure_IsNotHiddenAsNoIndex()
    {
        _ddb.Setup(d => d.GetRasterInfo(It.IsAny<string>())).Throws(new InvalidOperationException("a bug"));

        await Should.ThrowAsync<InvalidOperationException>(() => Create().GetAsync(Hash, Cog));
    }

    [Test]
    public async Task Get_AnInfoWithoutIndexLayer_IsNoIndexAndIsNotRemembered()
    {
        _info = """{"width":1,"height":1,"bandCount":1}""";
        var catalog = Create();

        (await catalog.GetAsync(Hash, Cog)).ShouldBeEmpty();
        _log.Count(LogLevel.Warning).ShouldBe(1, "libddb always writes indexLayer: its absence is not 'no index'");

        _info = FakeDdbWrapper.RasterInfo("NDVI");
        (await catalog.GetAsync(Hash, Cog)).ShouldBe(["NDVI"]);
    }

    [Test]
    public async Task Get_ACanceledCaller_StopsInsteadOfAnsweringNoIndex()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Create().GetAsync(Hash, Cog, canceled.Token));

        Reads().ShouldBe(0);
        _log.Count(LogLevel.Warning).ShouldBe(0);
    }

    [Test]
    public async Task Get_RunsTheNativeReadOnTheTilesLane()
    {
        var gate = new Mock<INativeWorkGate>();
        gate.Setup(g => g.RunAsync(NativeLane.Tiles, It.IsAny<Func<string>>(), It.IsAny<CancellationToken>()))
            .Returns((NativeLane _, Func<string> work, CancellationToken _) => Task.FromResult(work()));
        var catalog = new RasterIndexCatalog(_ddb.Object, TestCaches.Records(), _log, gate.Object);

        await catalog.GetAsync(Hash, Cog);

        gate.Verify(g => g.RunAsync(NativeLane.Tiles, It.IsAny<Func<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

/// <summary>The <c>indexLayer</c> member of a raster info document, and the fake's way to write one.</summary>
[TestFixture]
public class RasterIndexLayerTests
{
    [Test]
    public void Parse_KeepsLibddbsOrder()
    {
        RasterIndexLayer.Parse("""{"bandCount":5,"indexLayer":["NDVI","NDRE","NDWI","SAVI","EVI"]}""")
            .ShouldBe(["NDVI", "NDRE", "NDWI", "SAVI", "EVI"]);
    }

    [Test]
    public void Parse_AnEmptyArray_IsNoIndex()
    {
        RasterIndexLayer.Parse("""{"indexLayer":[]}""")!.ShouldBeEmpty();
    }

    [TestCase("""{"bandCount":3}""")]
    [TestCase("""{"indexLayer":null}""")]
    [TestCase("""{"indexLayer":"NDVI"}""")]
    [TestCase("[]")]
    public void Parse_NoArray_IsNull_NotAnEmptyList(string json)
    {
        RasterIndexLayer.Parse(json).ShouldBeNull();
    }

    [Test]
    public void Parse_NotJson_Throws()
    {
        Should.Throw<JsonException>(() => RasterIndexLayer.Parse("not json"));
    }

    [Test]
    public void TheFake_ServesTheFixturesEmptyList_ForTheRgbContractRaster()
    {
        RasterIndexLayer.Parse(new FakeDdbWrapper().GetRasterInfo("/contract/ortho.tif"))!.ShouldBeEmpty();
    }

    [Test]
    public void TheFake_RasterInfo_IsTheFixtureWithTheGivenList()
    {
        var info = FakeDdbWrapper.RasterInfo("NDVI", "SAVI");

        RasterIndexLayer.Parse(info).ShouldBe(["NDVI", "SAVI"]);
        using var document = JsonDocument.Parse(info);
        document.RootElement.GetProperty("bandCount").GetInt32().ShouldBe(3, "the rest is the fixture's");
    }
}
