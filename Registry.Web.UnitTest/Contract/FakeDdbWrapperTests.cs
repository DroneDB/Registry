#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Shouldly;

namespace Registry.Web.Test.Contract;

/// <summary>
/// <see cref="FakeDdbWrapper"/> answers the calls the libddb contract fixtures record, with the
/// caller's paths and URLs, and refuses every other call (XI-09).
/// </summary>
[TestFixture]
public sealed class FakeDdbWrapperTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "contract-ds");
    private static readonly string GenericRoot = Root.Replace('\\', '/');

    private const string CollectionRoot = "https://registry.test/orgs/acme/ds/survey";
    private const string CatalogRoot = "https://registry.test";

    private static readonly string[] AddedFiles = ["a.txt", "cloud.las", "layer.geojson", "ortho.tif", "sub"];

    private readonly FakeDdbWrapper _ddb = new();

    /// <summary>One fake call per fixture: a vendored fixture the fake does not serve fails <see cref="EveryFixture_IsServed"/>.</summary>
    private static readonly Dictionary<string, Func<FakeDdbWrapper, object?>> Calls = new()
    {
        ["add"] = d => d.Add(Root, AddedFiles.Select(p => Path.Combine(Root, p)).ToArray(), true),
        ["build-status"] = d => d.GetBuildStatus(Root, ["ortho.tif", "layer.geojson", "a.txt", "missing.tif"]),
        ["get-drone-image"] = d => d.GetEntry(Root, "DJI_0018.JPG"),
        ["get-folder"] = d => d.GetEntry(Root, "sub"),
        ["get-image"] = d => d.GetEntry(Root, "sub/img.jpg"),
        ["info-drone-image"] = d => d.Info(Path.Combine(Root, "DJI_0018.JPG"), withHash: true),
        ["info-generic"] = d => d.Info(Path.Combine(Root, "a.txt"), withHash: true),
        ["info-georaster"] = d => d.Info(Path.Combine(Root, "ortho.tif"), withHash: true),
        ["info-image"] = d => d.Info(Path.Combine(Root, "sub", "img.jpg"), withHash: true),
        ["info-pointcloud"] = d => d.Info(Path.Combine(Root, "cloud.las"), withHash: true),
        ["info-vector"] = d => d.Info(Path.Combine(Root, "layer.geojson"), withHash: true),
        ["list"] = d => d.List(Root, Root, true),
        ["list-folder"] = d => d.List(Root, Path.Combine(Root, "sub")),
        ["list-entries"] = d => d.ListEntries(Root, EntryQuery.All),
        ["list-entry-summaries"] = d => d.ListEntrySummaries(Root, EntryQuery.All),
        ["list-entries-filtered"] = d => d.ListEntrySummaries(Root,
            new EntryQuery(NamePattern: "*.TIF", Types: [EntryType.Image, EntryType.GeoRaster])),
        ["meta-add"] = d => d.MetaAdd(Root, "tags", "\"survey\""),
        ["meta-dump"] = d => d.MetaDump(Root),
        ["meta-get"] = d => d.MetaGet(Root, "annotation", "sub/img.jpg"),
        ["meta-get-list"] = d => d.MetaGet(Root, "tags"),
        ["meta-list"] = d => d.MetaList(Root),
        ["meta-list-entry"] = d => d.MetaList(Root, "sub/img.jpg"),
        ["meta-remove"] = d => d.MetaRemove(Root, d.MetaAdd(Root, "tags", "\"survey\"").Id),
        ["meta-set"] = d => d.MetaSet(Root, "annotation", """{ "level": 2, "note": "tree" }""", "sub/img.jpg"),
        ["meta-unset"] = d => d.MetaUnset(Root, "name"),
        ["pending-build-info-none"] = d => d.GetPendingBuildInfo(Root),
        ["raster-info"] = d => d.GetRasterInfo(Path.Combine(Root, "ortho.tif")),
        ["raster-metadata"] = d => d.GetRasterMetadata(Path.Combine(Root, "ortho.tif")),
        ["raster-metadata-formula"] = d => d.GetRasterMetadata(Path.Combine(Root, "ortho.tif"), "VARI", "RGB"),
        ["stac-collection"] = d => d.Stac(Root, null, CollectionRoot, "survey", CatalogRoot),
        ["stac-item-collection"] = d => d.StacItemCollection(Root, CollectionRoot, "survey", CatalogRoot, null, null, 10, 0),
        ["stac-item-drone-image"] = d => d.Stac(Root, "DJI_0018.JPG", CollectionRoot, "survey", CatalogRoot),
        ["stac-item-georaster"] = d => d.Stac(Root, "ortho.tif", CollectionRoot, "survey", CatalogRoot),
        ["stac-item-image"] = d => d.Stac(Root, "sub/img.jpg", CollectionRoot, "survey", CatalogRoot)
    };

    // Recorded on another dataset (a Shapefile without .shx): read directly, not served by the fake
    private static readonly string[] NotServed = ["pending-build-info"];

    [Test]
    public void EveryFixture_IsServed()
    {
        Calls.Keys.Concat(NotServed).Order().ShouldBe(ContractFixture.Names.Order());
    }

    [TestCaseSource(nameof(ServedFixtures))]
    public void Call_AnswersFromItsFixture(string fixture)
    {
        Calls[fixture](_ddb).ShouldNotBeNull();
    }

    private static string[] ServedFixtures() => [.. Calls.Keys];

    [Test]
    public void List_DeserializesLikeTheNativeAdapter()
    {
        var entries = _ddb.List(Root, Root, true);

        entries.Select(e => e.Path).ShouldBe(["a.txt", "cloud.las", "layer.geojson", "ortho.tif", "sub", "sub/img.jpg"]);
        var image = entries.Single(e => e.Path == "sub/img.jpg");
        image.Type.ShouldBe(EntryType.GeoImage);
        image.Depth.ShouldBe(1);
        image.Size.ShouldBe(1151);
        image.ModifiedTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime);
        image.Properties["make"].ShouldBe("Contract");
        image.PointGeometry.ShouldBeOfType<JObject>();
        entries.Single(e => e.Path == "sub").Type.ShouldBe(EntryType.Directory);
    }

    [Test]
    public void Info_UsesTheCallersAbsolutePath()
    {
        var path = Path.Combine(Root, "a.txt");

        var entry = _ddb.Info(path, withHash: true).ShouldHaveSingleItem();

        entry.Path.ShouldBe("file://" + GenericRoot + "/a.txt");
        entry.Depth.ShouldBe(FakeDdbWrapper.PathDepth(path));
        entry.Hash.ShouldBe("457ca8f0e0b21bbad706ae2c3f7bfaace967212b6c8cbf5cfeb478e388560ff7");
    }

    [TestCase("/a.txt", 0)]
    [TestCase("/tmp/ds/a.txt", 2)]
    [TestCase("C:/data/ds/a.txt", 2)]
    [TestCase(@"C:\data\a.txt", 1)]
    public void PathDepth_CountsTheFoldersBelowTheRoot(string path, int depth)
    {
        FakeDdbWrapper.PathDepth(path).ShouldBe(depth);
    }

    [Test]
    public void GetEntry_NotInTheIndex_IsNull_IndexedWithoutFixture_IsNotSupported()
    {
        _ddb.GetEntry(Root, "nope.tif").ShouldBeNull();
        Should.Throw<NotSupportedException>(() => _ddb.GetEntry(Root, "a.txt"));
    }

    [Test]
    public void Meta_TheSameItemHasTheSameIdInEveryOutput()
    {
        var added = _ddb.MetaAdd(Root, "tags", "\"survey\"");
        Guid.TryParse(added.Id, out _).ShouldBeTrue();

        JArray.Parse(_ddb.MetaGet(Root, "tags")!).Select(i => (string?)i["id"]).ShouldContain(added.Id);
        _ddb.MetaDump(Root).Single(m => m.Key == "tags" && (string?)JToken.Parse(m.Data.ToString()) == "survey")
            .Id.ShouldBe(added.Id);
        _ddb.MetaRemove(Root, added.Id).ShouldBe(1);
        Should.Throw<NotSupportedException>(() => _ddb.MetaRemove(Root, Guid.NewGuid().ToString()));
    }

    [Test]
    public void MetaSet_ComparesTheDataAsJson()
    {
        var meta = _ddb.MetaSet(Root, "annotation", """{"note":"tree","level":2}""", "sub/img.jpg");

        meta.Data["note"]!.ToString().ShouldBe("tree");
        Should.Throw<NotSupportedException>(() => _ddb.MetaSet(Root, "annotation", """{"note":"bush"}""", "sub/img.jpg"));
    }

    [Test]
    public void Stac_UsesTheCallersRootsAndId()
    {
        var collection = _ddb.Stac(Root, "", CollectionRoot, "survey", CatalogRoot);
        var item = _ddb.Stac(Root, "sub/img.jpg", CollectionRoot, "survey", CatalogRoot);

        collection["id"]!.ToString().ShouldBe("survey");
        collection.ToString().ShouldNotContain("hub.example");
        collection["links"]!.Select(l => l["href"]!.ToString()).ShouldContain(CatalogRoot + "/stac");
        collection["links"]!.Select(l => l["href"]!.ToString()).ShouldContain(CollectionRoot + "/stac");
        item["collection"]!.ToString().ShouldBe("survey");
        item["id"]!.ToString().ShouldBe("path-sub-img-jpg");
        item["assets"]!["sub/img.jpg"]!["href"]!.ToString().ShouldBe(CollectionRoot + "/download?path=sub%2Fimg.jpg");
    }

    [Test]
    public void BuildStatus_AnswersAnyRecordedPaths_InTheCallersOrder()
    {
        var statuses = _ddb.GetBuildStatus(Root, ["a.txt", "ortho.tif"]);

        statuses.Select(s => (s.Path, s.Buildable, s.Complete)).ShouldBe([("a.txt", false, false), ("ortho.tif", true, true)]);
        _ddb.IsBuildable(Root, "layer.geojson").ShouldBeTrue();
        _ddb.IsBuildComplete(Root, "layer.geojson").ShouldBeFalse();
        _ddb.IsBuildPending(Root).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => _ddb.GetBuildStatus(Root, ["cloud.las"]));
    }

    [Test]
    public void PendingBuildFixture_OfTheShapefileDataset_ReadsDirectly()
    {
        var pending = ContractFixture.Load("pending-build-info").Output.ToObject<PendingBuildInfo[]>()!;

        pending.ShouldHaveSingleItem().MissingDependencies.ShouldBe(["roads.shx"]);
    }

    [Test]
    public void Raster_ReturnsTheLibddbJson()
    {
        JObject.Parse(_ddb.GetRasterInfo(Path.Combine(Root, "ortho.tif")))["bandCount"]!.Value<int>().ShouldBe(3);
        Should.Throw<NotSupportedException>(() => _ddb.GetRasterInfo(Path.Combine(Root, "other.tif")));
    }

    [Test]
    public void UnrecordedCalls_Throw_NeverDefaults()
    {
        Should.Throw<NotSupportedException>(() => _ddb.GetStamp(Root)).Message.ShouldContain("IDdbWrapper.GetStamp");
        Should.Throw<NotSupportedException>(() => _ddb.Init(Root));
        Should.Throw<NotSupportedException>(() => _ddb.List(Root, Path.Combine(Root, "sub"), true))
            .Message.ShouldContain("no libddb contract fixture records List(");
        Should.Throw<NotSupportedException>(() => _ddb.Info(Path.Combine(Root, "a.txt")));
        Should.Throw<NotSupportedException>(() => _ddb.StacItemCollection(Root, CollectionRoot, "survey", CatalogRoot, null, null, 5, 0));
    }

    [Test]
    public void VersionAndMimeTypes_MatchTheNativeAdapter()
    {
        // The parameterless NativeDdbWrapper does not call libddb
        var native = new NativeDdbWrapper();

        _ddb.TileMimeType.ShouldBe(native.TileMimeType);
        _ddb.ThumbnailMimeType.ShouldBe(native.ThumbnailMimeType);
        _ddb.GetVersion().ShouldBe(FakeDdbWrapper.Version);
    }

    [Test]
    public void MockOverTheFake_OverridesOneCall_AndKeepsTheFixtures()
    {
        var mock = new Mock<FakeDdbWrapper> { CallBase = true };
        mock.Setup(d => d.MetaGet(It.IsAny<string>(), "visibility", It.IsAny<string?>())).Returns("{\"data\":2}");
        var ddb = mock.Object;

        ddb.MetaGet(Root, "visibility").ShouldBe("{\"data\":2}");
        ddb.MetaList(Root).Count.ShouldBe(3);
    }
}
