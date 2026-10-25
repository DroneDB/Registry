#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// The libddb 2.x contract: one test per export of the ABI that
/// Registry uses beyond the original wrapper, called through the P/Invoke declarations of <see cref="NativeDdb"/>, the adapter's one
/// native-methods home (UTF-8 strings, 1-byte bools, outputs
/// freed with DDBFree), with the outputs checked against DroneDB's contract fixtures
/// (tests/contract/fixtures, named in each test) and the documented error codes.
/// <para>
/// <b>The gate.</b> The fixture runs the adapter's startup handshake (<see cref="DdbAbi.Verify"/>):
/// ABI 2.x (major equal to <see cref="DdbAbi.Major"/>, minor at least <see cref="DdbAbi.Minor"/>) and
/// every export the adapter declares. Registry runs on libddb 2.x only (DroneDB has one version and
/// the ABI follows it: a removed export is a new major), so a library that fails the handshake fails the fixture; a wrong result fails its test.
/// DroneDB's registry_contract job (the freshly built .deb) and Registry's CI (the pinned DroneDB
/// build) run every test.
/// </para>
/// <para>
/// DDBShutdown is process-final, so it runs in a child process (<see cref="LibddbShutdownProbe"/>).
/// The tests that change process-wide state (DDBConfigure, DDBSetLogCallback) put it back in a
/// finally block. Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~LibddbContractTests"
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public unsafe class LibddbContractTests : TestBase
{
    private const string BaseTestFolder = nameof(LibddbContractTests);

    // A 20x20 GeoTIFF (EPSG:26711): a GeoRaster that builds a COG in a moment
    private static readonly string GeoRasterUrl = TestData.Url("testdata/raster/byte.tif");

    // tests/contract/inputs/a.txt and the roads.shp of the pending fixtures, byte for byte, and the
    // mtime of the contract dataset: the entries get the fixtures' hashes, sizes and mtimes
    private const string TextHash = "457ca8f0e0b21bbad706ae2c3f7bfaace967212b6c8cbf5cfeb478e388560ff7";
    private const string ShapefileName = "roads.shp";
    private const string ShapefileContent = "not a real shapefile: its .shx and .dbf are missing";
    private const string ShapefileHash = "afe1bd97fb41ff4dd3fc4134a9c32e95ed6a1a0af81c20e88e20b92bbc73a30c";

    private const string RootPlaceholder = "<root>";

    // fixtures/build-artifacts.json, copy-build-artifacts.json: the artifact of a built GeoRaster
    private const string CogArtifactJson = """
        {
          "downloadName": "cog.tif",
          "kind": "file",
          "mediaType": "image/tiff; application=geotiff; profile=cloud-optimized",
          "path": "cog/cog.tif",
          "role": "raster.cog"
        }
        """;

    // fixtures/build-artifacts.json: the build object of an entry that is not buildable
    private const string NotBuildableJson = """
        {
          "artifacts": [],
          "buildable": false,
          "hash": null,
          "lastAttempt": null,
          "missingDependencies": [],
          "retryAfter": null,
          "root": null,
          "source": null,
          "state": "none"
        }
        """;

    // fixtures/capabilities.json: entries of lists that only ever grow
    private static readonly string[] ErrorReasons = ["idleTimeout", "lowDiskSpace", "noSpaceOnDevice"];
    private static readonly string[] CancellableExports = ["DDBBuild", "DDBExportRaster2"];

    private static readonly NativeDdbWrapper DdbWrapper = new();

    // The downloaded GeoRaster (TestDownloadCache, as TempFile does)
    private string? _geoRaster;

    [OneTimeSetUp]
    public void RequireLibddb2()
    {
        UseDdbFolder();

        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        // The first call of every DDB process (GDAL drivers, PROJ data): ABI 2.0 returns DDBErr
        Ok(NativeDdb.DDBRegisterProcess(false), "DDBRegisterProcess");
        TestContext.Progress.WriteLine($"{nameof(LibddbContractTests)}: {message}, libddb {DdbWrapper.GetVersion()}");
        _geoRaster = TestDownloadCache.GetOrDownload(GeoRasterUrl, Path.Combine(Path.GetTempPath(), BaseTestFolder));
    }

    [OneTimeTearDown]
    public void DeleteDownloads()
    {
        if (_geoRaster != null)
            TestDownloadCache.Evict(_geoRaster);
    }

    #region Handshake: DDBGetAbiVersion, DDBGetCapabilities, DDBRegisterProcess, DDBGetLastErrorReason

    [Test]
    public void GetAbiVersion_IsAbi2_AndTheCapabilitiesAgree()
    {
        var version = NativeDdb.DDBGetAbiVersion();
        var (major, minor) = DdbAbi.Decode(version);

        major.ShouldBe(DdbAbi.Major);
        minor.ShouldBeGreaterThanOrEqualTo(DdbAbi.Minor);
        version.ShouldBe(((uint)major << 16) | (uint)minor);

        var abi = Capabilities()["abi"]!;
        ((int)abi["major"]!).ShouldBe(major);
        ((int)abi["minor"]!).ShouldBe(minor);
    }

    // fixtures/capabilities.json (its "version" is normalised; the lists only ever grow)
    [Test]
    public void GetCapabilities_HasTheErrorCodesOfDdbResult_TheReasons_AndASortedSymbolList()
    {
        // Built once, owned by the library: the same pointer on every call
        NativeDdb.DDBGetCapabilities().ShouldBe(NativeDdb.DDBGetCapabilities());

        var capabilities = Capabilities();
        ((string?)capabilities["version"]).ShouldBe(DdbWrapper.GetVersion());

        // Every DdbResult code under its ddb.h name, with its value
        var codes = capabilities["errorCodes"]!.ToObject<Dictionary<string, int>>()!;
        foreach (var (name, code) in ErrorCodeNames)
            codes.ShouldContainKeyAndValue(name, (int)code, $"DDBERR_{name}");

        var reasons = Strings(capabilities["errorReasons"]);
        reasons.SequenceEqual(reasons.Order(StringComparer.Ordinal)).ShouldBeTrue("errorReasons is sorted");
        ErrorReasons.ShouldBeSubsetOf(reasons);

        var symbols = Symbols();
        symbols.SequenceEqual(symbols.Order(StringComparer.Ordinal)).ShouldBeTrue("symbols is sorted (ddb.symbols)");
        var cancellable = Strings(capabilities["cancellable"]);
        CancellableExports.ShouldBeSubsetOf(cancellable);
        cancellable.ShouldBeSubsetOf(symbols, "a cancellable export is an export");
    }

    /// <summary>
    /// Every libddb export Registry.Adapters declares (every LibraryImport of <see cref="NativeDdb"/>) exists in the loaded library and is listed in "symbols": one
    /// failure that names every missing export.
    /// </summary>
    [Test]
    public void EveryExportRegistryDeclares_ResolvesAndIsListedInTheCapabilities()
    {
        var declared = DeclaredExports();
        declared.ShouldContain(nameof(NativeDdb.DDBApplyDeltaEx), "the LibraryImport stubs are found");
        declared.ShouldContain("DDBAddWithHints", "the wrapper's DllImports are found");

        NativeLibrary.TryLoad("ddb", typeof(NativeDdbWrapper).Assembly, null, out var library)
            .ShouldBeTrue("libddb loads as the DllImports load it");
        try
        {
            declared.Where(name => !NativeLibrary.TryGetExport(library, name, out _))
                .ShouldBeEmpty("exports Registry declares that the loaded libddb does not export");
        }
        finally
        {
            NativeLibrary.Free(library); // the DllImports keep their own reference
        }

        declared.Except(Symbols()).ShouldBeEmpty("exports Registry declares that DDBGetCapabilities does not list");
    }

    [Test]
    public void RegisterProcess_AgainReturnsNone()
    {
        Ok(NativeDdb.DDBRegisterProcess(false), "DDBRegisterProcess");
        LastError().ShouldBe("");
    }

    [Test]
    public void GetLastErrorReason_IsEmptyAfterASuccessAndForAFailureWithoutAReason()
    {
        Ok(NativeDdb.DDBNormalizePath("a", null, out var output), "DDBNormalizePath");
        TakeString(output);
        LastErrorReason().ShouldBe("");

        Fails(NativeDdb.DDBNormalizePath(null, null, out _), DdbResult.InvalidArgs, "DDBNormalizePath(NULL)");
        LastErrorReason().ShouldBe("", "an invalid argument is not transient");
        // The accessors never touch the last error
        LastError().ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    #region Paths and STAC: DDBNormalizePath, DDBStacData, DDBStacItemCollectionData

    // fixtures/normalize-path.json, normalize-path-rejected.json; vectors/entry-paths.json
    [TestCase("photos\\2024/./a.jpg", "[\".uploads\"]", """{"path": "photos/2024/a.jpg"}""")]
    [TestCase(".uploads/x", "[\".uploads\"]", """{"rejected": "reserved"}""")]
    [TestCase("photos/.uploads/x", "[\".uploads\"]", """{"path": "photos/.uploads/x"}""")]
    [TestCase("fotografie/città/ß.jpg", null, """{"path": "fotografie/città/ß.jpg"}""")]
    [TestCase("", null, """{"rejected": "empty"}""")]
    [TestCase("./", "", """{"rejected": "empty"}""")]
    [TestCase("/etc/passwd", null, """{"rejected": "absolute"}""")]
    [TestCase("C:/x", null, """{"rejected": "absolute"}""")]
    [TestCase("a/../b", null, """{"rejected": "traversal"}""")]
    [TestCase("sub/.ddb/x", null, """{"rejected": "reserved"}""")]
    public void NormalizePath_MatchesTheFixturesAndVectors(string path, string? reservedJson, string expected)
    {
        Ok(NativeDdb.DDBNormalizePath(path, reservedJson, out var output), "DDBNormalizePath");
        ShouldEqualJson(TakeJson(output), ParseJson(expected));
    }

    [TestCase(null, null)]
    [TestCase("a", "not json")]
    [TestCase("a", "[\"a/b\"]")]
    [TestCase("a", "[\"..\"]")]
    public void NormalizePath_InvalidArguments_AreInvalidArgs(string? path, string? reservedJson)
    {
        Fails(NativeDdb.DDBNormalizePath(path, reservedJson, out _), DdbResult.InvalidArgs, "DDBNormalizePath");
    }

    // fixtures/stac-data-collection.json, stac-data-item-georaster.json (README "Data-only STAC")
    [Test]
    public void StacData_HasNoIdsLinksOrHrefs_AndNotFoundForAMissingEntry()
    {
        using var area = new TestArea(nameof(StacData_HasNoIdsLinksOrHrefs_AndNotFoundForAMissingEntry));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);

        Ok(NativeDdb.DDBStacData(root, null, out var output), "DDBStacData(collection)");
        var collection = (JObject)TakeJson(output);
        ((string?)collection["type"]).ShouldBe("Collection");
        ((string?)collection["stac_version"]).ShouldBe("1.1.0");
        ((string?)collection["title"]).ShouldBe("dataset", "no dataset name: the folder name");
        ((string?)collection["license"]).ShouldBe("proprietary");
        collection["extent"]!["spatial"]!["bbox"].ShouldNotBeNull();
        // The entries that are not Items (no geometry): a.txt, not the GeoRaster
        ShouldEqualJson(collection["assets"]!, ParseJson("""{"a.txt": {"title": "a.txt"}}"""));
        ShouldBeDataOnly(collection);

        Ok(NativeDdb.DDBStacData(root, GeoRasterName, out output), "DDBStacData(item)");
        var item = (JObject)TakeJson(output);
        item.Properties().Select(p => p.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["assets", "bbox", "geometry", "properties", "stac_extensions", "stac_version", "type"]);
        ((string?)item["type"]).ShouldBe("Feature");
        ((string?)item["properties"]!["title"]).ShouldBe(GeoRasterName, "properties.title is the entry path");
        ((string?)item["properties"]!["datetime"]).ShouldBe("2023-11-14T22:13:20Z", "the datetime of the entry mtime");
        ShouldEqualJson(item["assets"]![GeoRasterName]!,
            ParseJson("""{"roles": ["data"], "title": "byte.tif", "type": "image/tiff"}"""));
        ShouldEqualJson(item["assets"]!["thumbnail"]!,
            ParseJson("""{"roles": ["thumbnail"], "title": "Thumbnail", "type": "image/webp"}"""));
        ShouldBeDataOnly(item);

        Fails(NativeDdb.DDBStacData(root, "missing.tif", out _), DdbResult.NotFound, "DDBStacData(missing)");
        Fails(NativeDdb.DDBStacData(null, null, out _), DdbResult.InvalidArgs, "DDBStacData(NULL)");
    }

    // fixtures/stac-data-item-collection.json
    [Test]
    public void StacItemCollectionData_IsTheItemCollectionWithoutLinks()
    {
        using var area = new TestArea(nameof(StacItemCollectionData_IsTheItemCollectionWithoutLinks));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);

        Ok(NativeDdb.DDBStacItemCollectionData(root, "", "", 10, 0, out var output), "DDBStacItemCollectionData");
        var collection = (JObject)TakeJson(output);
        ((string?)collection["type"]).ShouldBe("FeatureCollection");
        var features = collection["features"]!.Children<JObject>().ToArray();
        ((int)collection["numberReturned"]!).ShouldBe(features.Length);
        ((int)collection["numberMatched"]!).ShouldBeGreaterThanOrEqualTo(features.Length);
        features.Select(f => (string?)f["properties"]!["title"]).ShouldContain(GeoRasterName);
        features.ShouldAllBe(f => (string?)f["type"] == "Feature");
        ShouldBeDataOnly(collection);

        Fails(NativeDdb.DDBStacItemCollectionData(root, "1,2,3", "", 10, 0, out _), DdbResult.InvalidArgs,
            "DDBStacItemCollectionData(bbox of 3 numbers)");
        Fails(NativeDdb.DDBStacItemCollectionData(null, "", "", 10, 0, out _), DdbResult.InvalidArgs,
            "DDBStacItemCollectionData(NULL)");
    }

    #endregion

    #region Build manifest: DDBGetEntryTypes, DDBGetBuildArtifacts, DDBResolveBuildFile, DDBCopyBuildArtifacts

    // fixtures/entry-types.json
    [Test]
    public void GetEntryTypes_NamesEveryEntryTypeOfRegistry_WithItsRules()
    {
        Ok(NativeDdb.DDBGetEntryTypes(out var output), "DDBGetEntryTypes");
        var types = TakeJson(output).Children<JObject>().ToDictionary(t => (int)t["id"]!);

        foreach (var type in Enum.GetValues<EntryType>())
        {
            types.ShouldContainKey((int)type, $"EntryType.{type}");
            // Case-insensitive, as DDBRescan takes them (EntryType.Geovideo is "GeoVideo")
            ((string?)types[(int)type]["name"]).ShouldNotBeNull().ToUpperInvariant()
                .ShouldBe(type.ToString().ToUpperInvariant(), $"EntryType.{type}");
        }

        ShouldEqualJson(types[(int)EntryType.GeoRaster], ParseJson("""
            {"buildable": true, "id": 4, "name": "GeoRaster", "roles": ["raster.cog"],
             "sidecarExtensions": [], "thumbnail": "file"}
            """));
        ShouldEqualJson(types[(int)EntryType.Generic], ParseJson("""
            {"buildable": false, "id": 2, "name": "Generic", "roles": [], "sidecarExtensions": [], "thumbnail": "none"}
            """));
        types[(int)EntryType.Vector]["sidecarExtensions"]!.Values<string>().ShouldBe(["cpg", "dbf", "shx", "prj"]);
        types[(int)EntryType.PointCloud]["roles"]!.Values<string>().ShouldBe(["pointcloud.copc", "pointcloud.ept"]);
    }

    // The members the Hub's entryTypes feature (GET /sys/features, EntryTypeDto) passes through: libddb adding, renaming
    // or dropping one is a change of that wire shape, so it is noticed here
    [Test]
    public void GetEntryTypes_HasExactlyTheMembersTheFeaturesPassThrough()
    {
        Ok(NativeDdb.DDBGetEntryTypes(out var output), "DDBGetEntryTypes");

        foreach (var type in TakeJson(output).Children<JObject>())
            type.Properties().Select(p => p.Name).ShouldBe(
                ["buildable", "id", "name", "roles", "sidecarExtensions", "thumbnail"], ignoreOrder: true,
                $"entry type {type["name"]}");
    }

    // fixtures/build-artifacts.json
    [Test]
    public void GetBuildArtifacts_ReportsBuiltNotBuildableAndMissingEntries()
    {
        using var area = new TestArea(nameof(GetBuildArtifacts_ReportsBuiltNotBuildableAndMissingEntries));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);
        DdbWrapper.Build(root, GeoRasterName);
        var hash = GeoRasterHash();

        var artifacts = GetBuildArtifacts(root, GeoRasterName, TextName, "missing.tif");

        ShouldEqualJson(Normalize(artifacts[0], root), ParseJson($$"""
            {
              "build": {
                "artifacts": [{{CogArtifactJson}}],
                "buildable": true,
                "hash": "{{hash}}",
                "lastAttempt": null,
                "missingDependencies": [],
                "retryAfter": null,
                "root": "<root>/.ddb/build/{{hash}}",
                "source": "{{GeoRasterName}}",
                "state": "complete"
              },
              "exists": true,
              "hash": "{{hash}}",
              "path": "{{GeoRasterName}}",
              "type": 4
            }
            """));
        ShouldEqualJson(artifacts[1], ParseJson($$"""
            {"build": {{NotBuildableJson}}, "exists": true, "hash": "{{TextHash}}", "path": "a.txt", "type": 2}
            """));
        ShouldEqualJson(artifacts[2], ParseJson("""{"exists": false, "path": "missing.tif"}"""));

        Ok(NativeDdb.DDBGetBuildArtifacts(root, [], 0, out var output), "DDBGetBuildArtifacts(0 paths)");
        ShouldEqualJson(TakeJson(output), new JArray());

        Fails(NativeDdb.DDBGetBuildArtifacts(root, [TextName], -1, out _), DdbResult.InvalidArgs,
            "DDBGetBuildArtifacts(numPaths -1)");
        Fails(NativeDdb.DDBGetBuildArtifacts(root, [TextName, null], 2, out _), DdbResult.InvalidArgs,
            "DDBGetBuildArtifacts(a NULL path)");
        Fails(NativeDdb.DDBGetBuildArtifacts("", [TextName], 1, out _), DdbResult.InvalidArgs,
            "DDBGetBuildArtifacts(empty ddbPath)");
    }

    // fixtures/build-artifacts-pending.json: the same roads.shp, so the same hash
    [Test]
    public void GetBuildArtifacts_DeferredBuild_IsPendingWithItsMissingDependencies()
    {
        using var area = new TestArea(nameof(GetBuildArtifacts_DeferredBuild_IsPendingWithItsMissingDependencies));
        var root = Path.Combine(area.TestFolder, "dataset");
        Directory.CreateDirectory(root);
        DdbWrapper.Init(root);
        DdbWrapper.Add(root, WriteContractFile(root, ShapefileName, ShapefileContent));
        DdbWrapper.Build(root, ShapefileName); // DDBERR_BUILDDEPMISSING: deferred

        var artifacts = GetBuildArtifacts(root, ShapefileName);
        var build = artifacts[0]["build"]!;
        build["lastAttempt"]!.Type.ShouldBe(JTokenType.Integer);
        build["retryAfter"]!.Type.ShouldBe(JTokenType.Integer);
        var lastAttempt = build["lastAttempt"]!.Value<long>();
        var retryAfter = build["retryAfter"]!.Value<long>();
        retryAfter.ShouldBe(lastAttempt + PendingMarkers.LibddbRetryBackoffSeconds);
        // DDBGetPendingBuildInfo reports the same marker, through the adapter's port type
        var marker = DdbWrapper.GetPendingBuildInfo(root).ShouldHaveSingleItem();
        (marker.Path, marker.LastAttempt, marker.RetryAfter).ShouldBe((ShapefileName, lastAttempt, retryAfter));
        build["lastAttempt"] = 0;
        build["retryAfter"] = 0;

        ShouldEqualJson(Normalize(artifacts, root), ParseJson($$"""
            [{
              "build": {
                "artifacts": [],
                "buildable": true,
                "hash": "{{ShapefileHash}}",
                "lastAttempt": 0,
                "missingDependencies": ["roads.shx"],
                "retryAfter": 0,
                "root": "<root>/.ddb/build/{{ShapefileHash}}",
                "source": "roads.shp",
                "state": "pending"
              },
              "exists": true,
              "hash": "{{ShapefileHash}}",
              "path": "roads.shp",
              "type": 14
            }]
            """));
    }

    // fixtures/resolve-build-file.json
    [Test]
    public void ResolveBuildFile_ReturnsTheArtifactPath_AndRejectsPathsOutsideTheBuild()
    {
        using var area = new TestArea(nameof(ResolveBuildFile_ReturnsTheArtifactPath_AndRejectsPathsOutsideTheBuild));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);
        DdbWrapper.Build(root, GeoRasterName);
        var hash = GeoRasterHash();
        var expected = Path.GetFullPath(Path.Combine(root, ".ddb", "build", hash, "cog", "cog.tif"));

        foreach (var path in new[] { "cog/cog.tif", "cog\\cog.tif" })
        {
            Ok(NativeDdb.DDBResolveBuildFile(root, hash, path, out var output), $"DDBResolveBuildFile({path})");
            var resolved = TakeString(output);
            Path.GetFullPath(resolved).ShouldBe(expected);
            File.Exists(resolved).ShouldBeTrue();
        }

        Fails(NativeDdb.DDBResolveBuildFile(root, hash, "cog/missing.tif", out _), DdbResult.NotFound,
            "DDBResolveBuildFile(a file that does not exist)");
        Fails(NativeDdb.DDBResolveBuildFile(root, new string('0', 64), "cog/cog.tif", out _), DdbResult.NotFound,
            "DDBResolveBuildFile(a hash no entry has)");
        Fails(NativeDdb.DDBResolveBuildFile(root, hash, "../cog/cog.tif", out _), DdbResult.InvalidArgs,
            "DDBResolveBuildFile(traversal)");
        Fails(NativeDdb.DDBResolveBuildFile(root, hash, "/cog/cog.tif", out _), DdbResult.InvalidArgs,
            "DDBResolveBuildFile(absolute)");
        Fails(NativeDdb.DDBResolveBuildFile(root, "xyz", "cog/cog.tif", out _), DdbResult.InvalidArgs,
            "DDBResolveBuildFile(malformed hash)");
        Fails(NativeDdb.DDBResolveBuildFile(root, hash, null, out _), DdbResult.InvalidArgs,
            "DDBResolveBuildFile(NULL path)");
    }

    // fixtures/copy-build-artifacts.json
    [Test]
    public void CopyBuildArtifacts_CopiesTheBuildToAnotherDataset()
    {
        using var area = new TestArea(nameof(CopyBuildArtifacts_CopiesTheBuildToAnotherDataset));
        var source = CreateDataset(area, "source", withGeoRaster: true);
        var destination = CreateDataset(area, "destination", withGeoRaster: true);
        DdbWrapper.Build(source, GeoRasterName);
        var hash = GeoRasterHash();

        Ok(NativeDdb.DDBCopyBuildArtifacts(source, destination, hash, out var output), "DDBCopyBuildArtifacts");
        ShouldEqualJson(TakeJson(output), ParseJson($$"""
            {"artifacts": [{{CogArtifactJson}}], "hash": "{{hash}}", "pending": false}
            """));
        File.Exists(Path.Combine(destination, ".ddb", "build", hash, "cog", "cog.tif")).ShouldBeTrue();
        ((string?)GetBuildArtifacts(destination, GeoRasterName)[0]["build"]!["state"]).ShouldBe("complete");

        Fails(NativeDdb.DDBCopyBuildArtifacts(source, destination, TextHash, out _), DdbResult.NotFound,
            "DDBCopyBuildArtifacts(the hash of an entry that is not buildable)");
        Fails(NativeDdb.DDBCopyBuildArtifacts(source, source, hash, out _), DdbResult.InvalidArgs,
            "DDBCopyBuildArtifacts(same dataset)");
        Fails(NativeDdb.DDBCopyBuildArtifacts(source, destination, "xyz", out _), DdbResult.InvalidArgs,
            "DDBCopyBuildArtifacts(malformed hash)");
    }

    /// <summary>DDBERR_RETRYABLE and its reason: the destination volume is below minFreeDiskMb.</summary>
    [Test]
    public void CopyBuildArtifacts_BelowTheDiskBudget_IsRetryableWithTheReasonLowDiskSpace()
    {
        using var area = new TestArea(nameof(CopyBuildArtifacts_BelowTheDiskBudget_IsRetryableWithTheReasonLowDiskSpace));
        var source = CreateDataset(area, "source", withGeoRaster: true);
        var destination = CreateDataset(area, "destination", withGeoRaster: true);
        DdbWrapper.Build(source, GeoRasterName);

        Ok(NativeDdb.DDBConfigure("""{"minFreeDiskMb": 10000000000000}"""), "DDBConfigure(minFreeDiskMb 10^13)");
        try
        {
            Fails(NativeDdb.DDBCopyBuildArtifacts(source, destination, GeoRasterHash(), out _), DdbResult.Retryable,
                "DDBCopyBuildArtifacts(no free space)");
            LastErrorReason().ShouldBe("lowDiskSpace");
        }
        finally
        {
            Ok(NativeDdb.DDBConfigure("""{"minFreeDiskMb": null}"""), "DDBConfigure(reset)");
        }
    }

    #endregion

    #region Raster info: DDBGetRasterInfo "indexLayer"

    // The five single-band UInt16 captures of a MicaSense RedEdge shot (test_data multispectral/IMG_0180_1..5.tif)
    private static readonly string[] MultispectralBandUrls =
        [.. Enumerable.Range(1, 5).Select(i => TestData.Url($"multispectral/IMG_0180_{i}.tif"))];

    // fixtures/raster-info.json (the RGB contract raster: "indexLayer": []), tests/contract/schemas/raster-info.schema.json
    [Test]
    public void GetRasterInfo_IndexLayer_IsAnEmptyList_ForASingleBandRaster()
    {
        var info = JObject.Parse(DdbWrapper.GetRasterInfo(_geoRaster!));

        info["indexLayer"].ShouldBeOfType<JArray>("libddb always writes the list: [] is its 'none'");
        ((JArray)info["indexLayer"]!).ShouldBeEmpty();
        RasterIndexLayer.Parse(DdbWrapper.GetRasterInfo(_geoRaster!))!.ShouldBeEmpty();
    }

    // ddb.h DDBGetRasterInfo: five or more unnamed data bands are read as R, G, B, RedEdge, NIR; SAVI and EVI need
    // floating-point data, and these bands are UInt16: libddb lists the three ratio indices, in its order, upper-case
    [Test]
    public void GetRasterInfo_IndexLayer_IsTheListLibddbRenders_ForAFiveBandIntegerRaster()
    {
        using var area = new TestArea(nameof(GetRasterInfo_IndexLayer_IsTheListLibddbRenders_ForAFiveBandIntegerRaster));
        var cache = Path.Combine(Path.GetTempPath(), BaseTestFolder);
        var bands = MultispectralBandUrls.Select(u => TestDownloadCache.GetOrDownload(u, cache)).ToArray();
        try
        {
            var merged = Path.Combine(area.TestFolder, "merged.tif");
            DdbWrapper.MergeMultispectral(bands, merged);

            var json = DdbWrapper.GetRasterInfo(merged);
            ((string?)JObject.Parse(json)["dataType"]).ShouldBe("UInt16");
            RasterIndexLayer.Parse(json).ShouldBe(["NDVI", "NDRE", "NDWI"]);
        }
        finally
        {
            foreach (var band in bands)
                TestDownloadCache.Evict(band);
        }
    }

    #endregion

    #region Process: DDBConfigure, DDBGetEffectiveConfig, cancel tokens, DDBSetLogCallback, DDBSetThreadOperation

    // fixtures/effective-config.json
    [Test]
    public void Configure_AndGetEffectiveConfig_MatchTheFixture_AndAnInvalidCallChangesNothing()
    {
        using var area = new TestArea(nameof(Configure_AndGetEffectiveConfig_MatchTheFixture_AndAnInvalidCallChangesNothing));
        var cache = Path.Combine(area.TestFolder, "cache");
        Directory.CreateDirectory(cache);

        Ok(NativeDdb.DDBConfigure(new JObject
        {
            ["cacheDir"] = cache, ["cpuPoolSize"] = 3, ["gdalCacheMb"] = 256, ["ioThreads"] = 2, ["minFreeDiskMb"] = 256
        }.ToString(Formatting.None)), "DDBConfigure");
        try
        {
            var config = EffectiveConfig();
            ((int)config["hardwareThreads"]!).ShouldBeGreaterThanOrEqualTo(1);
            config["hardwareThreads"] = 0;
            config["settings"]!["cacheDir"]!.Type.ShouldBe(JTokenType.String);
            Path.GetFullPath((string)config["settings"]!["cacheDir"]!).ShouldBe(Path.GetFullPath(cache));
            config["settings"]!["cacheDir"] = "<cache>";
            ShouldEqualJson(config, ParseJson("""
                {
                  "hardwareThreads": 0,
                  "settings": {"cacheDir": "<cache>", "cpuPoolSize": 3, "gdalCacheMb": 256, "ioThreads": 2,
                               "minFreeDiskMb": 256},
                  "sources": {"cacheDir": "configure", "cpuPoolSize": "configure", "gdalCacheMb": "configure",
                              "ioThreads": "configure", "minFreeDiskMb": "configure"}
                }
                """));

            var before = EffectiveConfig();
            foreach (var invalid in new[]
                     {
                         null, "[]", "not json", """{"cpuPoolSizee": 1}""", """{"ioThreads": 0}""",
                         """{"cpuPoolSize": 1, "minFreeDiskMb": -1}""", """{"cacheDir": "relative/folder"}""",
                         """{"gdalCacheMb": 15}""", """{"gdalCacheMb": 1048577}""", """{"gdalCacheMb": "256"}"""
                     })
                Fails(NativeDdb.DDBConfigure(invalid), DdbResult.InvalidArgs, $"DDBConfigure({invalid ?? "NULL"})");
            ShouldEqualJson(EffectiveConfig(), before, "an invalid DDBConfigure changes nothing");
        }
        finally
        {
            Ok(NativeDdb.DDBConfigure(
                """{"cacheDir": null, "cpuPoolSize": null, "gdalCacheMb": null, "ioThreads": null, "minFreeDiskMb": null}"""),
                "DDBConfigure(reset)");
        }

        ((JObject)EffectiveConfig()["sources"]!).Properties().Select(p => (string?)p.Value)
            .ShouldNotContain("configure", "null puts a setting back");
    }

    [Test]
    public void CancelToken_CanceledBeforeACancellableCall_StopsItWithCanceled_ForThatCallOnly()
    {
        using var area = new TestArea(nameof(CancelToken_CanceledBeforeACancellableCall_StopsItWithCanceled_ForThatCallOnly));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);

        Ok(NativeDdb.DDBCreateCancelToken(out var token), "DDBCreateCancelToken");
        token.ShouldNotBe(IntPtr.Zero);
        try
        {
            // A live token: the call works
            Ok(NativeDdb.DDBSetThreadCancelToken(token), "DDBSetThreadCancelToken");
            DdbWrapper.Build(root, GeoRasterName, force: true);

            Ok(NativeDdb.DDBCancel(token), "DDBCancel");
            Ok(NativeDdb.DDBCancel(token), "DDBCancel(again)");

            // DDBBuild is "cancellable" (capabilities); NativeDdbWrapper.Build calls it first on this thread
            Ok(NativeDdb.DDBSetThreadCancelToken(token), "DDBSetThreadCancelToken");
            Should.Throw<DdbCanceledException>(() => DdbWrapper.Build(root, GeoRasterName, force: true))
                .Message.ShouldContain("Canceled by the cancel token");

            // The canceled call reset the thread's token: the next call works
            DdbWrapper.Build(root, GeoRasterName, force: true);
        }
        finally
        {
            Ok(NativeDdb.DDBFreeCancelToken(token), "DDBFreeCancelToken");
        }

        Fails(NativeDdb.DDBCancel(IntPtr.Zero), DdbResult.InvalidArgs, "DDBCancel(NULL)");
        Ok(NativeDdb.DDBFreeCancelToken(IntPtr.Zero), "DDBFreeCancelToken(NULL)");
        Ok(NativeDdb.DDBSetThreadCancelToken(IntPtr.Zero), "DDBSetThreadCancelToken(NULL)");
    }

    [Test]
    public void SetLogCallback_ReceivesGdalErrorsWithTheOperationIdOfTheCall()
    {
        var log = new LogCollector();
        var handle = GCHandle.Alloc(log);
        Ok(NativeDdb.DDBSetLogCallback(&OnLog, (int)DdbLogLevel.Warning, GCHandle.ToIntPtr(handle)),
            "DDBSetLogCallback");
        try
        {
            using var area = new TestArea(nameof(SetLogCallback_ReceivesGdalErrorsWithTheOperationIdOfTheCall));
            var notARaster = Path.Combine(area.TestFolder, "not-a-raster.tif");
            File.WriteAllText(notARaster, "not a raster");

            Ok(NativeDdb.DDBSetThreadOperation(42), "DDBSetThreadOperation");
            Should.Throw<DdbException>(() => DdbWrapper.GetRasterInfo(notARaster));
            // The operation id was for that call only
            Should.Throw<DdbException>(() => DdbWrapper.GetRasterInfo(notARaster));
        }
        finally
        {
            // Returns once no thread is inside the callback any more
            Ok(NativeDdb.DDBSetLogCallback(null, 0, IntPtr.Zero), "DDBSetLogCallback(NULL)");
            handle.Free();
        }

        var records = log.Records;
        records.ShouldNotBeEmpty("GDAL's error on the unreadable raster reaches the callback");
        records.ShouldAllBe(r => r.Level >= (int)DdbLogLevel.Warning && r.Level <= (int)DdbLogLevel.Fatal);
        records.ShouldAllBe(r => r.Source == "gdal" || r.Source == "ddb");
        string.Join(",", records.Select(r => r.OpId).Distinct().Order())
            .ShouldBe("0,42", "the first call logs with operation 42, the second with none");

        Fails(NativeDdb.DDBSetLogCallback(&OnLog, (int)DdbLogLevel.Fatal + 1, IntPtr.Zero), DdbResult.InvalidArgs,
            "DDBSetLogCallback(minLevel 6)");
        Fails(NativeDdb.DDBSetLogCallback(&OnLog, -1, IntPtr.Zero), DdbResult.InvalidArgs,
            "DDBSetLogCallback(minLevel -1)");
    }

    /// <summary>
    /// DDBShutdown is process-final, so <see cref="LibddbShutdownProbe"/> runs it in a child
    /// process (this test assembly started with its probe argument) and reports each step.
    /// </summary>
    [Test]
    public void Shutdown_RunsInAChildProcess_RefusesLaterCalls_AndKeepsTheExemptOnes()
    {
        var assembly = typeof(LibddbShutdownProbe).Assembly.Location;
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
            ArgumentList = { "exec", assembly, LibddbShutdownProbe.Argument }
        };

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {dotnet}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(true);
            Assert.Fail("the shutdown probe did not exit in 2 minutes");
        }

        var output = $"exit code {process.ExitCode}\n--- stdout\n{stdout.GetAwaiter().GetResult()}\n" +
                     $"--- stderr\n{stderr.GetAwaiter().GetResult()}";
        TestContext.Out.WriteLine(output);

        process.ExitCode.ShouldBe(0, output);
        stdout.Result.ShouldContain(LibddbShutdownProbe.Passed, Case.Sensitive, output);
    }

    #endregion

    #region Delta: DDBApplyDeltaEx

    [Test]
    public void ApplyDeltaEx_TrustsOnlyHashesOfTheDelta_AndAppliesIt()
    {
        using var area = new TestArea(nameof(ApplyDeltaEx_TrustsOnlyHashesOfTheDelta_AndAppliesIt));
        var source = CreateDataset(area, "source", withGeoRaster: false);
        var destination = Path.Combine(area.TestFolder, "destination");
        Directory.CreateDirectory(destination);
        DdbWrapper.Init(destination);

        var delta = DdbWrapper.Delta(DdbWrapper.GetStamp(source), DdbWrapper.GetStamp(destination));
        delta.Adds.Select(a => (a.Path, a.Hash)).ToArray().ShouldBe(new[] { (TextName, TextHash) });
        var deltaJson = NativeJson.Serialize(delta);

        foreach (var invalid in new[]
                 {
                     "not json",
                     "[\"a.txt\"]",
                     $$"""{"a.txt": "{{Sha256("other")}}"}""", // not the delta's hash
                     $$"""{"a.txt": "{{TextHash.ToUpperInvariant()}}"}""", // exactly the delta's hash only
                     $$"""{"a.txt": "{{TextHash}}", "b.txt": "{{TextHash}}"}""" // b.txt is not in the delta
                 })
        {
            Fails(NativeDdb.DDBApplyDeltaEx(deltaJson, source, destination, (int)MergeStrategy.KeepTheirs, "[]",
                invalid, out _), DdbResult.InvalidArgs, $"DDBApplyDeltaEx({invalid})");
            File.Exists(Path.Combine(source, TextName)).ShouldBeTrue("an invalid call changes nothing");
            File.Exists(Path.Combine(destination, TextName)).ShouldBeFalse("an invalid call changes nothing");
        }

        Fails(NativeDdb.DDBApplyDeltaEx(null, source, destination, (int)MergeStrategy.KeepTheirs, "[]", null, out _),
            DdbResult.InvalidArgs, "DDBApplyDeltaEx(NULL delta)");

        Ok(NativeDdb.DDBApplyDeltaEx(deltaJson, source, destination, (int)MergeStrategy.KeepTheirs, "[]",
            $$"""{"a.txt": "{{TextHash}}"}""", out var conflicts), "DDBApplyDeltaEx");
        ShouldEqualJson(TakeJson(conflicts), new JArray());
        File.ReadAllText(Path.Combine(destination, TextName)).ShouldBe(TextContent);
        DdbWrapper.GetEntry(destination, TextName)!.Hash.ShouldBe(TextHash);
    }

    #endregion

    #region Helpers

    // DdbResult -> its DDBERR_ name in ddb.h and in the capabilities' "errorCodes"
    private static readonly Dictionary<string, DdbResult> ErrorCodeNames = new()
    {
        ["NONE"] = DdbResult.Success,
        ["EXCEPTION"] = DdbResult.Exception,
        ["BUILDDEPMISSING"] = DdbResult.BuildDependencyMissing,
        ["BUILDINPROGRESS"] = DdbResult.BuildInProgress,
        ["CANCELED"] = DdbResult.Canceled,
        ["BUSY"] = DdbResult.Busy,
        ["NOTFOUND"] = DdbResult.NotFound,
        ["INVALIDARGS"] = DdbResult.InvalidArgs,
        ["RETRYABLE"] = DdbResult.Retryable,
        ["SHUTDOWN"] = DdbResult.Shutdown
    };

    [Test]
    public void ErrorCodeNames_CoverEveryDdbResult() =>
        ErrorCodeNames.Values.Order().ShouldBe(Enum.GetValues<DdbResult>().Order());

    private static JObject Capabilities()
    {
        var ptr = NativeDdb.DDBGetCapabilities();
        ptr.ShouldNotBe(IntPtr.Zero);
        // Owned by the library: not freed
        return (JObject)ParseJson(Marshal.PtrToStringUTF8(ptr)!);
    }

    private static string[] Symbols() => Strings(Capabilities()["symbols"]);

    private static string[] Strings(JToken? array) => array.ShouldNotBeNull().Values<string>().OfType<string>().ToArray();

    /// <summary>
    /// The libddb entry points Registry.Adapters declares: every [DllImport("ddb")] method of the
    /// assembly, the ones LibraryImport generates included.
    /// </summary>
    private static string[] DeclaredExports() =>
        typeof(NativeDdbWrapper).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Import: m.GetCustomAttribute<DllImportAttribute>()))
            .Where(m => m.Import?.Value == "ddb")
            .Select(m => m.Import!.EntryPoint ?? m.Method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static JObject EffectiveConfig()
    {
        Ok(NativeDdb.DDBGetEffectiveConfig(out var output), "DDBGetEffectiveConfig");
        return (JObject)TakeJson(output);
    }

    private static JArray GetBuildArtifacts(string root, params string[] paths)
    {
        Ok(NativeDdb.DDBGetBuildArtifacts(root, paths, paths.Length, out var output), "DDBGetBuildArtifacts");
        var artifacts = (JArray)TakeJson(output);
        artifacts.Count.ShouldBe(paths.Length, "one element per input path");
        return artifacts;
    }

    /// <summary>
    /// A dataset folder <paramref name="name"/> with the contract's a.txt and, optionally, the
    /// GeoRaster, both with the contract mtime, indexed.
    /// </summary>
    private string CreateDataset(TestArea area, string name, bool withGeoRaster) =>
        LibddbNative.CreateDataset(DdbWrapper, area, name, withGeoRaster ? _geoRaster : null);

    private static string WriteContractFile(string root, string name, string content) =>
        LibddbNative.WriteContractFile(root, name, content);

    private string GeoRasterHash()
    {
        using var stream = File.OpenRead(_geoRaster!);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The fixtures' &lt;root&gt; placeholder (README "Normalisation"): the dataset folder in its native and generic form, then '\' as '/'.</summary>
    private static JToken Normalize(JToken token, string root)
    {
        var copy = token.DeepClone();
        var generic = root.Replace('\\', '/');
        IEnumerable<JToken> tokens = copy is JContainer container ? container.DescendantsAndSelf() : [copy];
        foreach (var value in tokens.OfType<JValue>().Where(v => v.Type == JTokenType.String).ToArray())
        {
            var text = (string)value.Value!;
            if (!text.Contains(root, StringComparison.Ordinal) && !text.Contains(generic, StringComparison.Ordinal))
                continue;
            value.Value = text.Replace(root, RootPlaceholder, StringComparison.Ordinal)
                .Replace(generic, RootPlaceholder, StringComparison.Ordinal).Replace('\\', '/');
        }

        return copy;
    }

    /// <summary>No member built from a URL or an id: no "id", "collection", "links" or "href" anywhere.</summary>
    private static void ShouldBeDataOnly(JObject document)
    {
        var urlMembers = new[] { "href", "links" };
        foreach (var property in document.DescendantsAndSelf().OfType<JObject>().SelectMany(o => o.Properties()))
            urlMembers.ShouldNotContain(property.Name, $"at {property.Path}");

        foreach (var owner in new[] { document }.Concat(document["features"]?.Children<JObject>() ?? []))
        {
            owner.ContainsKey("id").ShouldBeFalse($"\"id\" at {owner.Path}");
            owner.ContainsKey("collection").ShouldBeFalse($"\"collection\" at {owner.Path}");
        }
    }

    private static void ShouldEqualJson(JToken actual, JToken expected, string? because = null)
    {
        if (!JToken.DeepEquals(actual, expected))
            Assert.Fail($"{because ?? "JSON differs"}\nexpected: {expected.ToString(Formatting.None)}\nactual:   {actual.ToString(Formatting.None)}");
    }

    private sealed record LogRecord(int Level, string Message, string Source, ulong OpId);

    private sealed class LogCollector
    {
        private readonly List<LogRecord> _records = [];
        private readonly object _lock = new();

        public void Add(LogRecord record)
        {
            lock (_lock)
                _records.Add(record);
        }

        public LogRecord[] Records
        {
            get
            {
                lock (_lock)
                    return [.. _records];
            }
        }
    }

    /// <summary>The <c>DDBLogCallback</c>: may run on any thread; an exception must never leave it.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int level, byte* message, byte* source, ulong opId, IntPtr userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is LogCollector log)
                log.Add(new LogRecord(level, Marshal.PtrToStringUTF8((IntPtr)message) ?? "",
                    Marshal.PtrToStringUTF8((IntPtr)source) ?? "", opId));
        }
        catch
        {
            // Never unwind into libddb
        }
    }

    #endregion
}
