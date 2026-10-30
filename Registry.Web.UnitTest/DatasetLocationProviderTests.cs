using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services;
using Registry.Web.Services.Datasets;
using Registry.Web.Test.Support;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// The location of a dataset: libddb's extent when it has one, else the box of the positions of its geo-referenced
/// entries; read once per state of the index and kept (also when there is none).
/// </summary>
[TestFixture]
public class DatasetLocationProviderTests
{
    private const string Org = "org";
    private static readonly Guid Ref = Guid.NewGuid();

    private Mock<IDDB> _ddb = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private string? _fingerprint;
    private JsonElement _stac;
    private List<Entry> _entries = null!;
    private DatasetLocationProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _fingerprint = "v1";
        _stac = Collection("[[10,20,30,40]]");
        _entries = [];

        _ddb = new Mock<IDDB>();
        _ddb.Setup(d => d.GetIndexFileState()).Returns(() =>
            _fingerprint == null ? null : new IndexFileState(_fingerprint, DateTime.UtcNow));
        _ddb.Setup(d => d.GetStacData(null)).Returns(() => _stac);
        _ddb.Setup(d => d.Find(It.IsAny<EntryQuery>())).Returns(() => _entries);

        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Open(Org, Ref)).Returns(_ddb.Object);

        _provider = new DatasetLocationProvider(_ddbManager.Object, TestCaches.Records(),
            NullLogger<DatasetLocationProvider>.Instance);
    }

    private static JsonElement Collection(string bbox) =>
        TestJson.Parse("{\"extent\":{\"spatial\":{\"bbox\":" + bbox + "}}}");

    private static Entry PointEntry(double lon, double lat) => new()
    {
        Path = $"p{lon}.jpg", Type = EntryType.GeoImage,
        PointGeometry = TestJson.Parse($"{{\"type\":\"Point\",\"coordinates\":[{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},12.5]}}")
    };

    [Test]
    public async Task TheExtentOfTheIndex_IsTheLocation_AndNoEntryIsRead()
    {
        var location = await _provider.GetAsync(Org, Ref);

        location.ShouldNotBeNull().Bbox.ShouldBe([10, 20, 30, 40]);
        _ddb.Verify(d => d.Find(It.IsAny<EntryQuery>()), Times.Never);
    }

    [Test]
    public async Task AnExtentOfSixValues_IsFlattenedToItsFootprint()
    {
        _stac = Collection("[[10,20,-5,30,40,500]]");

        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([10, 20, 30, 40]);
    }

    [TestCase("[[0,0,0,0]]")]
    [TestCase("[[0,0,0,0,0,0]]")]
    [TestCase("[]")]
    [TestCase("[[1,2,3]]")]
    [TestCase("[[\"a\",2,3,4]]")]
    [TestCase("[[10,20,300,40]]")]
    [TestCase("[[30,20,10,40]]")]
    public async Task AnExtentWithoutLocation_FallsBackToTheEntries(string bbox)
    {
        _stac = Collection(bbox);
        _entries = [PointEntry(11, 45), PointEntry(12.5, 46)];

        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([11, 45, 12.5, 46]);
    }

    [Test]
    public async Task AStacDocumentWithoutExtent_FallsBackToTheEntries()
    {
        _stac = TestJson.Parse("{}");
        _entries = [PointEntry(11, 45)];

        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([11, 45, 11, 45]);
    }

    [Test]
    public async Task TheFallbackAsksForTheEntriesThatHaveAPosition_AndUnionsPositionsAndFootprints()
    {
        _stac = Collection("[[0,0,0,0]]");
        _entries =
        [
            PointEntry(11, 45),
            new Entry
            {
                Path = "pano.jpg", Type = EntryType.GeoPanorama,
                PolygonGeometry = TestJson.Parse("{\"type\":\"Polygon\",\"coordinates\":[[[9,44],[13,44],[13,47],[9,47],[9,44]]]}")
            },
            new Entry { Path = "no-geometry.jpg", Type = EntryType.GeoImage }
        ];

        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([9, 44, 13, 47]);

        _ddb.Verify(d => d.Find(It.Is<EntryQuery>(q =>
            q.Path == null && q.Types != null &&
            q.Types.OrderBy(t => t).SequenceEqual(new[] { EntryType.GeoImage, EntryType.GeoPanorama, EntryType.Geovideo }
                .OrderBy(t => t)))), Times.Once);
    }

    [Test]
    public async Task ADatasetWithoutAnyGeometry_HasNoLocation()
    {
        _stac = Collection("[[0,0,0,0]]");
        _entries = [new Entry { Path = "a.jpg", Type = EntryType.GeoImage }];

        (await _provider.GetAsync(Org, Ref)).ShouldBeNull();
    }

    [Test]
    public async Task TheSameIndexState_IsReadOnce_AndANewOneIsReadAgain()
    {
        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([10, 20, 30, 40]);
        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([10, 20, 30, 40]);
        _ddb.Verify(d => d.GetStacData(null), Times.Once);

        _fingerprint = "v2";
        _stac = Collection("[[1,2,3,4]]");

        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([1, 2, 3, 4]);
        _ddb.Verify(d => d.GetStacData(null), Times.Exactly(2));
    }

    [Test]
    public async Task AnAnswerOfNoLocation_IsKeptToo()
    {
        _stac = Collection("[[0,0,0,0]]");

        (await _provider.GetAsync(Org, Ref)).ShouldBeNull();
        (await _provider.GetAsync(Org, Ref)).ShouldBeNull();

        _ddb.Verify(d => d.GetStacData(null), Times.Once);
        _ddb.Verify(d => d.Find(It.IsAny<EntryQuery>()), Times.Once);
    }

    [Test]
    public async Task WithoutAFingerprint_TheLocationIsReadEveryTime_AndNeverKept()
    {
        _fingerprint = null;

        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([10, 20, 30, 40]);
        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([10, 20, 30, 40]);

        _ddb.Verify(d => d.GetStacData(null), Times.Exactly(2));

        // And nothing stayed behind for the state that has a fingerprint
        _fingerprint = "v1";
        _stac = Collection("[[5,6,7,8]]");
        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([5, 6, 7, 8]);
    }

    [Test]
    public async Task AnIndexThatCannotBeOpened_HasNoLocation()
    {
        _ddbManager.Setup(m => m.Open(Org, Ref)).Throws(new System.IO.DirectoryNotFoundException("gone"));

        (await _provider.GetAsync(Org, Ref)).ShouldBeNull();
    }

    [Test]
    public async Task AnIndexThatCannotBeRead_HasNoLocation_AndIsReadAgainNextTime()
    {
        _ddb.Setup(d => d.GetStacData(null)).Throws(new DdbException("corrupt"));

        (await _provider.GetAsync(Org, Ref)).ShouldBeNull();

        _ddb.Setup(d => d.GetStacData(null)).Returns(_stac);
        (await _provider.GetAsync(Org, Ref)).ShouldNotBeNull().Bbox.ShouldBe([10, 20, 30, 40]);
    }

    [Test]
    public async Task ABusyIndex_IsAnErrorOfTheCaller_NotALackOfLocation()
    {
        _ddb.Setup(d => d.GetStacData(null)).Throws(new DdbBusyException("busy"));

        await Should.ThrowAsync<DdbBusyException>(() => _provider.GetAsync(Org, Ref));
    }

    [Test]
    public async Task ABusyIndexAtOpening_IsAnErrorOfTheCaller()
    {
        _ddbManager.Setup(m => m.Open(Org, Ref)).Throws(new DdbBuildInProgressException("building"));

        await Should.ThrowAsync<DdbBuildInProgressException>(() => _provider.GetAsync(Org, Ref));
    }
}
