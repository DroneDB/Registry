using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Services.Datasets;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// On the real libddb: where a dataset is. libddb's extent leaves out the entries that have a position and no footprint,
/// so a dataset of such photos has an all-zero extent and its location comes from the positions; a raster has its
/// extent.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~DatasetLocationNativeTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class DatasetLocationNativeTests
{
    private const string Org = "org";

    // An index of three geo-images that have a position and no footprint
    private static readonly string PointsOnlyIndexUrl = TestData.Url("ddb-remove-test/.ddb/dbase.sqlite");
    private static readonly string OrthophotoUrl = TestData.Url("brighton/odm_orthophoto.tif");

    private readonly NativeDdbWrapper _wrapper = new();
    private readonly Guid _ref = Guid.NewGuid();
    private string _downloads = null!;
    private string _root = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _downloads = Path.Combine(Path.GetTempPath(), nameof(DatasetLocationNativeTests));

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "dataset-location-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var folder in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                     .Where(d => File.Exists(Path.Combine(d, ".ddb", "dbase.sqlite"))))
            _wrapper.ReleaseConnections(folder);
        Directory.Delete(_root, true);
    }

    private (DatasetLocationProvider Provider, DDB Ddb) ProviderOver(string folder)
    {
        var ddb = new DDB(folder, _wrapper);
        var manager = new Mock<IDdbManager>();
        manager.Setup(m => m.Open(Org, _ref)).Returns(ddb);

        return (new DatasetLocationProvider(manager.Object, TestCaches.Records(), NullLogger<DatasetLocationProvider>.Instance), ddb);
    }

    [Test]
    public async Task APointsOnlyDataset_HasNoExtent_AndItsLocationIsTheBoxOfItsPositions()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "points")).FullName;
        var index = Directory.CreateDirectory(Path.Combine(folder, ".ddb")).FullName;
        File.Copy(TestDownloadCache.GetOrDownload(PointsOnlyIndexUrl, _downloads), Path.Combine(index, "dbase.sqlite"));
        var (provider, ddb) = ProviderOver(folder);

        // The premise: three geo-images with a position and no footprint, and an extent of zeros
        var entries = ddb.Find(EntryQuery.OfTypes(null, EntryType.GeoImage));
        entries.Count.ShouldBe(3);
        entries.ShouldAllBe(e => e.PointGeometry != null && e.PolygonGeometry == null);
        Registry.Web.Services.Stac.StacDocuments.SpatialBbox(ddb.GetStacData()).ShouldBe([0, 0, 0, 0]);

        var bbox = (await provider.GetAsync(Org, _ref)).ShouldNotBeNull().Bbox;

        bbox.Length.ShouldBe(4);
        bbox[0].ShouldBeLessThanOrEqualTo(bbox[2]);
        bbox[1].ShouldBeLessThanOrEqualTo(bbox[3]);
        bbox.Any(v => v != 0).ShouldBeTrue("not the all-zero extent");
        foreach (var point in entries.Select(e => GeoJsonBbox.Of(e.PointGeometry).ShouldNotBeNull()))
        {
            point[0].ShouldBeInRange(bbox[0], bbox[2]);
            point[1].ShouldBeInRange(bbox[1], bbox[3]);
        }

        // And the answer is kept for the state of the index
        (await provider.GetAsync(Org, _ref)).ShouldNotBeNull().Bbox.ShouldBe(bbox);
    }

    [Test]
    public async Task AnOrthophotoDataset_HasItsExtentAsLocation()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "ortho")).FullName;
        var (provider, ddb) = ProviderOver(folder);
        ddb.Init();
        File.Copy(TestDownloadCache.GetOrDownload(OrthophotoUrl, _downloads), Path.Combine(folder, "ortho.tif"));
        ddb.AddRaw("ortho.tif");

        var extent = Registry.Web.Services.Stac.StacDocuments.SpatialBbox(ddb.GetStacData()).ShouldNotBeNull();
        extent.Any(v => v != 0).ShouldBeTrue("a raster has an extent");

        var bbox = (await provider.GetAsync(Org, _ref)).ShouldNotBeNull().Bbox;

        bbox.ShouldBe(extent);
        bbox[0].ShouldBeLessThan(bbox[2]);
        bbox[1].ShouldBeLessThan(bbox[3]);
    }

    [Test]
    public async Task AnEmptyDataset_HasNoLocation()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var (provider, ddb) = ProviderOver(folder);
        ddb.Init();

        (await provider.GetAsync(Org, _ref)).ShouldBeNull();
    }
}
