#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using System.Text.Json;
using System.Text.Json.Nodes;
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


    private static readonly string[] AddedFiles = ["a.txt", "cloud.las", "layer.geojson", "ortho.tif", "sub"];

    // Another dataset, the destination of the recorded build copy
    private static readonly string OtherRoot = Path.Combine(Path.GetTempPath(), "contract-ds-2");

    // The hash of the built ortho.tif of the build-artifacts fixtures
    private const string BuiltHash = "d0d2b869615dca09ef0055f2014401bab298503e3e1f10e339f14f5788ea702f";

    private readonly FakeDdbWrapper _ddb = new();

    /// <summary>One fake call per fixture: a vendored fixture the fake does not serve fails <see cref="EveryFixture_IsServed"/>.</summary>
    private static readonly Dictionary<string, Func<FakeDdbWrapper, object?>> Calls = new()
    {
        ["add"] = d => d.Add(Root, AddedFiles.Select(p => Path.Combine(Root, p)).ToArray(), true),
        ["build-artifacts"] = d => d.GetBuildArtifacts(Root, ["ortho.tif", "layer.geojson", "cloud.las", "a.txt", "sub", "missing.tif"]),
        ["copy-build-artifacts"] = d => d.CopyBuildArtifacts(Root, OtherRoot, BuiltHash),
        ["entry-types"] = d => d.GetEntryTypes(),
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
        ["normalize-path"] = d => d.NormalizePath("photos\\2024/./a.jpg", [".uploads"]),
        ["normalize-path-rejected"] = d => d.NormalizePath(".uploads/x", [".uploads"]),
        ["pending-build-info-none"] = d => d.GetPendingBuildInfo(Root),
        ["resolve-build-file"] = d => d.ResolveBuildFile(Root, BuiltHash, "cog/cog.tif"),
        ["raster-info"] = d => d.GetRasterInfo(Path.Combine(Root, "ortho.tif")),
        ["raster-metadata"] = d => d.GetRasterMetadata(Path.Combine(Root, "ortho.tif")),
        ["raster-metadata-formula"] = d => d.GetRasterMetadata(Path.Combine(Root, "ortho.tif"), "VARI", "RGB"),
        ["stac-data-collection"] = d => d.StacData(Root, null),
        ["stac-data-item-collection"] = d => d.StacItemCollectionData(Root, null, null, 10, 0),
        ["stac-data-item-georaster"] = d => d.StacData(Root, "ortho.tif"),
        ["stac-data-item-image"] = d => d.StacData(Root, "sub/img.jpg")
    };

    /// <summary>The fixtures <see cref="FakeDdbRuntime"/> serves.</summary>
    private static readonly Dictionary<string, Func<FakeDdbRuntime, object?>> RuntimeCalls = new()
    {
        ["capabilities"] = r => r.Capabilities,
        ["effective-config"] = r => r.GetEffectiveConfig()
    };

    private static readonly string[] NotServed =
    [
        // Recorded on another dataset (a Shapefile without .shx): read directly
        "pending-build-info", "build-artifacts-pending",
        // A deprecated export Registry no longer calls (GetBuildArtifacts replaces it, XI-04)
        "build-status",
        // ABI 1.1 outputs with no port member yet: the listing with build objects (RQ-11)
        "list-entries-ex",
        // The STAC exports that build Registry's URLs, which Registry no longer calls (XI-09, RQ-12: the data-only
        // documents replace them and Registry builds every id, link and href)
        "stac-collection", "stac-item-collection", "stac-item-drone-image", "stac-item-georaster", "stac-item-image"
    ];

    [Test]
    public void EveryFixture_IsServed()
    {
        Calls.Keys.Concat(RuntimeCalls.Keys).Concat(NotServed).Order().ShouldBe(ContractFixture.Names.Order());
    }

    [TestCaseSource(nameof(ServedRuntimeFixtures))]
    public void RuntimeCall_AnswersFromItsFixture(string fixture)
    {
        RuntimeCalls[fixture](new FakeDdbRuntime()).ShouldNotBeNull();
    }

    private static string[] ServedRuntimeFixtures() => [.. RuntimeCalls.Keys];

    [TestCaseSource(nameof(ServedFixtures))]
    public void Call_AnswersFromItsFixture(string fixture)
    {
        Calls[fixture](_ddb).ShouldNotBeNull();
    }

    private static string[] ServedFixtures() => [.. Calls.Keys];

    [TestCase("normalize-path")]
    [TestCase("normalize-path-rejected")]
    public void NormalizePath_EqualsItsFixture(string fixture)
    {
        var expected = ContractFixture.Load(fixture).Output.Deserialize<NormalizedEntryPath>(NativeJson.Options);

        Calls[fixture](_ddb).ShouldBe(expected);
    }

    private static PathVector[] Vectors() => [.. PathVectors.All];

    // XI-09: the fake answers DDBNormalizePath with a copy of libddb's rule; every golden vector pins
    // it (Registry.Adapters.Ddb.Test runs the same vectors through native libddb)
    [TestCaseSource(nameof(Vectors))]
    public void NormalizePath_AnswersEveryGoldenVector(PathVector vector)
    {
        _ddb.NormalizePath(vector.Input, vector.Reserved).ShouldBe(vector.Expected);
    }

    [Test]
    public void PathVectors_AreAllLoaded()
    {
        // The vendored file has 54 vectors (DroneDB 2.0.0), 3 of them for the C++ API only (NUL)
        PathVectors.All.Count.ShouldBe(54);
        PathVectors.All.Count(v => !v.CString).ShouldBe(3);
    }

    [TestCase("[\"a/b\"]")]
    [TestCase("[\"..\"]")]
    [TestCase("[\".\"]")]
    [TestCase("[\"\"]")]
    public void NormalizePath_InvalidReservedName_IsAnArgumentError(string reservedJson)
    {
        var names = JsonSerializer.Deserialize<string[]>(reservedJson)!;
        Should.Throw<ArgumentException>(() => _ddb.NormalizePath("a", names));
    }

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
        image.Properties["make"].GetString().ShouldBe("Contract");
        image.PointGeometry!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
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

        JsonNode.Parse(_ddb.MetaGet(Root, "tags")!)!.AsArray().Select(i => (string?)i!["id"]).ShouldContain(added.Id);
        // The dump carries each value as JSON text: "survey" is the string "\"survey\""
        _ddb.MetaDump(Root).Single(m => m.Key == "tags" && (string?)JsonNode.Parse(m.Data!.Value.GetString()!) == "survey")
            .Id.ShouldBe(added.Id);
        _ddb.MetaRemove(Root, added.Id).ShouldBe(1);
        Should.Throw<NotSupportedException>(() => _ddb.MetaRemove(Root, Guid.NewGuid().ToString()));
    }

    [Test]
    public void MetaSet_ComparesTheDataAsJson()
    {
        var meta = _ddb.MetaSet(Root, "annotation", """{"note":"tree","level":2}""", "sub/img.jpg");

        meta.Data!.Value.GetProperty("note").GetString().ShouldBe("tree");
        Should.Throw<NotSupportedException>(() => _ddb.MetaSet(Root, "annotation", """{"note":"bush"}""", "sub/img.jpg"));
    }

    [Test]
    public void StacData_HasNoIdOrUrl_AndNamesAnUnnamedCollectionAfterTheDatasetFolder()
    {
        var collection = _ddb.StacData(Path.Combine("/datasets", "org", "d1b2"), null);
        var item = _ddb.StacData(Root, "sub/img.jpg");

        collection.GetProperty("title").GetString().ShouldBe("d1b2");
        collection.TryGetProperty("id", out _).ShouldBeFalse();
        collection.TryGetProperty("links", out _).ShouldBeFalse();
        item.TryGetProperty("id", out _).ShouldBeFalse();
        item.TryGetProperty("collection", out _).ShouldBeFalse();
        item.TryGetProperty("links", out _).ShouldBeFalse();
        item.GetProperty("assets").GetProperty("sub/img.jpg").TryGetProperty("href", out _).ShouldBeFalse();
        item.GetProperty("properties").GetProperty("title").GetString().ShouldBe("sub/img.jpg");
    }

    [Test]
    public void StacItemCollectionData_IsPagedAsLibddbPagesIt()
    {
        var page = _ddb.StacItemCollectionData(Root, null, null, 2, 1);

        page.GetProperty("numberMatched").GetInt32().ShouldBe(4);
        page.GetProperty("numberReturned").GetInt32().ShouldBe(2);
        page.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("properties").GetProperty("title").GetString())
            .ShouldBe(["layer.geojson", "ortho.tif"]);
    }

    [Test]
    public void UnrecordedCalls_Throw_NeverDefaults()
    {
        Should.Throw<NotSupportedException>(() => _ddb.GetStamp(Root)).Message.ShouldContain("IDdbWrapper.GetStamp");
        Should.Throw<NotSupportedException>(() => _ddb.Init(Root));
        Should.Throw<NotSupportedException>(() => _ddb.List(Root, Path.Combine(Root, "sub"), true))
            .Message.ShouldContain("no libddb contract fixture records List(");
        Should.Throw<NotSupportedException>(() => _ddb.Info(Path.Combine(Root, "a.txt")));
        Should.Throw<NotSupportedException>(() => _ddb.StacItemCollectionData(Root, "1,2,3,4", null, 10, 0));
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
