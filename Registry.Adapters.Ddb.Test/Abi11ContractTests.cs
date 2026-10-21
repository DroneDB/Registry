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
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.Abi11Native;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// The libddb ABI 1.1 contract (XI-01, XI-09; DroneDB P3-10): one test per export added in ABI
/// 1.1, called through the P/Invoke declarations of <see cref="NativeDdbAbi11"/> the way RQ-08
/// will call them (UTF-8 strings, 1-byte bools, <see cref="DdbListEntriesOptions"/>, outputs freed
/// with DDBFree), with the outputs checked against DroneDB's contract fixtures
/// (tests/contract/fixtures, named in each test) and the documented error codes.
/// <para>
/// <b>The gate (the ABI handshake decides, never a missing export).</b> The fixture first reads
/// <c>DDBGetAbiVersion</c>:
/// <list type="bullet">
/// <item>the export does not exist, or the library reports ABI 1.0: the library predates ABI 1.1
/// (libddb 1.12.x, the release Registry's own CI pins), so every test here is <b>inconclusive</b>
/// (Assert.Inconclusive in the one-time setup), not failed;</item>
/// <item>the library reports ABI 1.x with x &gt;= 1: the contract applies in full. Every ABI 1.1
/// export must exist and behave as documented: a missing export (EntryPointNotFoundException) or a
/// wrong result <b>fails</b>, as the rest of this suite does (DroneDBTests: a missing export fails,
/// it is never ignored);</item>
/// <item>another ABI major: the adapter cannot use the library, so the fixture <b>fails</b>.</item>
/// </list>
/// So DroneDB's registry_contract job (the freshly built .deb, ABI 1.1) runs every test, and
/// Registry's CI on libddb 1.12.2 reports them inconclusive until DdbVersion moves to 1.13.
/// </para>
/// <para>
/// DDBShutdown is process-final, so it runs in a child process (<see cref="Abi11ShutdownProbe"/>).
/// The tests that change process-wide state (DDBConfigure, DDBSetLogCallback) put it back in a
/// finally block. Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~Abi11ContractTests"
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public unsafe class Abi11ContractTests : TestBase
{
    private const string BaseTestFolder = nameof(Abi11ContractTests);

    // A 20x20 GeoTIFF (EPSG:26711): a GeoRaster that builds a COG in a moment
    private static readonly string GeoRasterUrl = TestData.Url("testdata/raster/byte.tif");
    private const string GeoRasterName = "byte.tif";

    // tests/contract/inputs/a.txt and the roads.shp of the pending fixtures, byte for byte, and the
    // mtime of the contract dataset: the entries get the fixtures' hashes, sizes and mtimes
    private const string TextName = "a.txt";
    private const string TextContent = "DroneDB contract fixture input\n";
    private const string TextHash = "457ca8f0e0b21bbad706ae2c3f7bfaace967212b6c8cbf5cfeb478e388560ff7";
    private const string ShapefileName = "roads.shp";
    private const string ShapefileContent = "not a real shapefile: its .shx and .dbf are missing";
    private const string ShapefileHash = "afe1bd97fb41ff4dd3fc4134a9c32e95ed6a1a0af81c20e88e20b92bbc73a30c";
    private static readonly DateTime ContractMtime = DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime;

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

    // fixtures/build-artifacts.json, list-entries-ex.json: the build object of an entry that is not buildable
    private const string NotBuildableJson = """
        {
          "artifacts": [],
          "buildable": false,
          "hash": null,
          "lastAttempt": null,
          "missingDependencies": [],
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
    public void RequireAbi11()
    {
        UseDdbFolder();

        var (gate, message) = CheckAbi();
        switch (gate)
        {
            case Gate.NotApplicable:
                Assert.Inconclusive(message);
                break;
            case Gate.Incompatible:
                Assert.Fail(message);
                break;
        }

        // The first call of every DDB process (GDAL drivers, PROJ data): ABI 1.1 returns DDBErr
        Ok(NativeDdbAbi11.DDBRegisterProcess(false), "DDBRegisterProcess");
        TestContext.Progress.WriteLine($"{nameof(Abi11ContractTests)}: {message}, libddb {DdbWrapper.GetVersion()}");
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
    public void GetAbiVersion_IsAbi1AtLeast1_1_AndTheCapabilitiesAgree()
    {
        var version = NativeDdbAbi11.DDBGetAbiVersion();
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
        NativeDdbAbi11.DDBGetCapabilities().ShouldBe(NativeDdbAbi11.DDBGetCapabilities());

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
    /// Every libddb export Registry.Adapters declares (the ABI 1.0 DllImports of the wrapper and the
    /// ABI 1.1 LibraryImports) exists in the loaded library and is listed in "symbols": one
    /// failure that names every missing export.
    /// </summary>
    [Test]
    public void EveryExportRegistryDeclares_ResolvesAndIsListedInTheCapabilities()
    {
        var declared = DeclaredExports();
        declared.ShouldContain(nameof(NativeDdbAbi11.DDBApplyDeltaEx), "the LibraryImport stubs are found");
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
        Ok(NativeDdbAbi11.DDBRegisterProcess(false), "DDBRegisterProcess");
        LastError().ShouldBe("");
    }

    [Test]
    public void GetLastErrorReason_IsEmptyAfterASuccessAndForAFailureWithoutAReason()
    {
        Ok(NativeDdbAbi11.DDBNormalizePath("a", null, out var output), "DDBNormalizePath");
        TakeString(output);
        LastErrorReason().ShouldBe("");

        Fails(NativeDdbAbi11.DDBNormalizePath(null, null, out _), DdbResult.InvalidArgs, "DDBNormalizePath(NULL)");
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
        Ok(NativeDdbAbi11.DDBNormalizePath(path, reservedJson, out var output), "DDBNormalizePath");
        ShouldEqualJson(TakeJson(output), ParseJson(expected));
    }

    [TestCase(null, null)]
    [TestCase("a", "not json")]
    [TestCase("a", "[\"a/b\"]")]
    [TestCase("a", "[\"..\"]")]
    public void NormalizePath_InvalidArguments_AreInvalidArgs(string? path, string? reservedJson)
    {
        Fails(NativeDdbAbi11.DDBNormalizePath(path, reservedJson, out _), DdbResult.InvalidArgs, "DDBNormalizePath");
    }

    // fixtures/stac-data-collection.json, stac-data-item-georaster.json (README "Data-only STAC")
    [Test]
    public void StacData_HasNoIdsLinksOrHrefs_AndNotFoundForAMissingEntry()
    {
        using var area = new TestArea(nameof(StacData_HasNoIdsLinksOrHrefs_AndNotFoundForAMissingEntry));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);

        Ok(NativeDdbAbi11.DDBStacData(root, null, out var output), "DDBStacData(collection)");
        var collection = (JObject)TakeJson(output);
        ((string?)collection["type"]).ShouldBe("Collection");
        ((string?)collection["stac_version"]).ShouldBe("1.1.0");
        ((string?)collection["title"]).ShouldBe("dataset", "no dataset name: the folder name");
        ((string?)collection["license"]).ShouldBe("proprietary");
        collection["extent"]!["spatial"]!["bbox"].ShouldNotBeNull();
        // The entries that are not Items (no geometry): a.txt, not the GeoRaster
        ShouldEqualJson(collection["assets"]!, ParseJson("""{"a.txt": {"title": "a.txt"}}"""));
        ShouldBeDataOnly(collection);

        Ok(NativeDdbAbi11.DDBStacData(root, GeoRasterName, out output), "DDBStacData(item)");
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

        Fails(NativeDdbAbi11.DDBStacData(root, "missing.tif", out _), DdbResult.NotFound, "DDBStacData(missing)");
        Fails(NativeDdbAbi11.DDBStacData(null, null, out _), DdbResult.InvalidArgs, "DDBStacData(NULL)");
    }

    // fixtures/stac-data-item-collection.json
    [Test]
    public void StacItemCollectionData_IsTheItemCollectionWithoutLinks()
    {
        using var area = new TestArea(nameof(StacItemCollectionData_IsTheItemCollectionWithoutLinks));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);

        Ok(NativeDdbAbi11.DDBStacItemCollectionData(root, "", "", 10, 0, out var output), "DDBStacItemCollectionData");
        var collection = (JObject)TakeJson(output);
        ((string?)collection["type"]).ShouldBe("FeatureCollection");
        var features = collection["features"]!.Children<JObject>().ToArray();
        ((int)collection["numberReturned"]!).ShouldBe(features.Length);
        ((int)collection["numberMatched"]!).ShouldBeGreaterThanOrEqualTo(features.Length);
        features.Select(f => (string?)f["properties"]!["title"]).ShouldContain(GeoRasterName);
        features.ShouldAllBe(f => (string?)f["type"] == "Feature");
        ShouldBeDataOnly(collection);

        Fails(NativeDdbAbi11.DDBStacItemCollectionData(root, "1,2,3", "", 10, 0, out _), DdbResult.InvalidArgs,
            "DDBStacItemCollectionData(bbox of 3 numbers)");
        Fails(NativeDdbAbi11.DDBStacItemCollectionData(null, "", "", 10, 0, out _), DdbResult.InvalidArgs,
            "DDBStacItemCollectionData(NULL)");
    }

    #endregion

    #region Build manifest: DDBGetEntryTypes, DDBGetBuildArtifacts, DDBResolveBuildFile, DDBCopyBuildArtifacts, DDBListEntriesEx

    // fixtures/entry-types.json
    [Test]
    public void GetEntryTypes_NamesEveryEntryTypeOfRegistry_WithItsRules()
    {
        Ok(NativeDdbAbi11.DDBGetEntryTypes(out var output), "DDBGetEntryTypes");
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

        Ok(NativeDdbAbi11.DDBGetBuildArtifacts(root, [], 0, out var output), "DDBGetBuildArtifacts(0 paths)");
        ShouldEqualJson(TakeJson(output), new JArray());

        Fails(NativeDdbAbi11.DDBGetBuildArtifacts(root, [TextName], -1, out _), DdbResult.InvalidArgs,
            "DDBGetBuildArtifacts(numPaths -1)");
        Fails(NativeDdbAbi11.DDBGetBuildArtifacts(root, [TextName, null], 2, out _), DdbResult.InvalidArgs,
            "DDBGetBuildArtifacts(a NULL path)");
        Fails(NativeDdbAbi11.DDBGetBuildArtifacts("", [TextName], 1, out _), DdbResult.InvalidArgs,
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
        artifacts[0]["build"]!["lastAttempt"]!.Type.ShouldBe(JTokenType.Integer);
        artifacts[0]["build"]!["lastAttempt"] = 0;

        ShouldEqualJson(Normalize(artifacts, root), ParseJson($$"""
            [{
              "build": {
                "artifacts": [],
                "buildable": true,
                "hash": "{{ShapefileHash}}",
                "lastAttempt": 0,
                "missingDependencies": ["roads.shx"],
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
            Ok(NativeDdbAbi11.DDBResolveBuildFile(root, hash, path, out var output), $"DDBResolveBuildFile({path})");
            var resolved = TakeString(output);
            Path.GetFullPath(resolved).ShouldBe(expected);
            File.Exists(resolved).ShouldBeTrue();
        }

        Fails(NativeDdbAbi11.DDBResolveBuildFile(root, hash, "cog/missing.tif", out _), DdbResult.NotFound,
            "DDBResolveBuildFile(a file that does not exist)");
        Fails(NativeDdbAbi11.DDBResolveBuildFile(root, new string('0', 64), "cog/cog.tif", out _), DdbResult.NotFound,
            "DDBResolveBuildFile(a hash no entry has)");
        Fails(NativeDdbAbi11.DDBResolveBuildFile(root, hash, "../cog/cog.tif", out _), DdbResult.InvalidArgs,
            "DDBResolveBuildFile(traversal)");
        Fails(NativeDdbAbi11.DDBResolveBuildFile(root, hash, "/cog/cog.tif", out _), DdbResult.InvalidArgs,
            "DDBResolveBuildFile(absolute)");
        Fails(NativeDdbAbi11.DDBResolveBuildFile(root, "xyz", "cog/cog.tif", out _), DdbResult.InvalidArgs,
            "DDBResolveBuildFile(malformed hash)");
        Fails(NativeDdbAbi11.DDBResolveBuildFile(root, hash, null, out _), DdbResult.InvalidArgs,
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

        Ok(NativeDdbAbi11.DDBCopyBuildArtifacts(source, destination, hash, out var output), "DDBCopyBuildArtifacts");
        ShouldEqualJson(TakeJson(output), ParseJson($$"""
            {"artifacts": [{{CogArtifactJson}}], "hash": "{{hash}}", "pending": false}
            """));
        File.Exists(Path.Combine(destination, ".ddb", "build", hash, "cog", "cog.tif")).ShouldBeTrue();
        ((string?)GetBuildArtifacts(destination, GeoRasterName)[0]["build"]!["state"]).ShouldBe("complete");

        Fails(NativeDdbAbi11.DDBCopyBuildArtifacts(source, destination, TextHash, out _), DdbResult.NotFound,
            "DDBCopyBuildArtifacts(the hash of an entry that is not buildable)");
        Fails(NativeDdbAbi11.DDBCopyBuildArtifacts(source, source, hash, out _), DdbResult.InvalidArgs,
            "DDBCopyBuildArtifacts(same dataset)");
        Fails(NativeDdbAbi11.DDBCopyBuildArtifacts(source, destination, "xyz", out _), DdbResult.InvalidArgs,
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

        Ok(NativeDdbAbi11.DDBConfigure("""{"minFreeDiskMb": 10000000000000}"""), "DDBConfigure(minFreeDiskMb 10^13)");
        try
        {
            Fails(NativeDdbAbi11.DDBCopyBuildArtifacts(source, destination, GeoRasterHash(), out _), DdbResult.Retryable,
                "DDBCopyBuildArtifacts(no free space)");
            LastErrorReason().ShouldBe("lowDiskSpace");
        }
        finally
        {
            Ok(NativeDdbAbi11.DDBConfigure("""{"minFreeDiskMb": null}"""), "DDBConfigure(reset)");
        }
    }

    // fixtures/list-entries-ex.json
    [Test]
    public void ListEntriesEx_OptionsLayout_AndTheBuildObjectOfEachEntry()
    {
        // ddb.h: sizeof 8, structSize at 0, recursive 4, withDetails 5, withBuild 6
        Unsafe.SizeOf<DdbListEntriesOptions>().ShouldBe(8);
        Marshal.SizeOf<DdbListEntriesOptions>().ShouldBe(8);
        Marshal.OffsetOf<DdbListEntriesOptions>(nameof(DdbListEntriesOptions.StructSize)).ShouldBe(0);
        Marshal.OffsetOf<DdbListEntriesOptions>(nameof(DdbListEntriesOptions.Recursive)).ShouldBe(4);
        Marshal.OffsetOf<DdbListEntriesOptions>(nameof(DdbListEntriesOptions.WithDetails)).ShouldBe(5);
        Marshal.OffsetOf<DdbListEntriesOptions>(nameof(DdbListEntriesOptions.WithBuild)).ShouldBe(6);
        DdbListEntriesOptions.Create(true, false, true).StructSize.ShouldBe(8u);

        using var area = new TestArea(nameof(ListEntriesEx_OptionsLayout_AndTheBuildObjectOfEachEntry));
        var root = CreateDataset(area, "dataset", withGeoRaster: true);
        DdbWrapper.Build(root, GeoRasterName);

        var withBuild = ListEntriesEx(root, DdbListEntriesOptions.Create(true, false, true));
        withBuild.Select(e => (string?)e["path"]).ShouldBe([TextName, GeoRasterName]);
        ShouldEqualJson(withBuild[0], ParseJson($$"""
            {"build": {{NotBuildableJson}}, "depth": 0, "hash": "{{TextHash}}", "mtime": 1700000000,
             "path": "a.txt", "size": 31, "type": 2}
            """));
        ((string?)withBuild[1]["build"]!["state"]).ShouldBe("complete");
        ShouldEqualJson(withBuild[1]["build"]!["artifacts"]!, new JArray(ParseJson(CogArtifactJson)));

        // withBuild false: DDBListEntries' output, without "build"
        var withoutBuild = ListEntriesEx(root, DdbListEntriesOptions.Create(true, false, false));
        ShouldEqualJson(withoutBuild, new JArray(withBuild.Select(e =>
        {
            var copy = (JObject)e.DeepClone();
            copy.Remove("build");
            return copy;
        })));

        var tooSmall = DdbListEntriesOptions.Create(true, false, true);
        tooSmall.StructSize = 4;
        Fails(NativeDdbAbi11.DDBListEntriesEx(root, "", "", "", tooSmall, out _), DdbResult.InvalidArgs,
            "DDBListEntriesEx(structSize 4)");

        // The struct of a later ABI (structSize 16): accepted while the fields this library does not know are zero
        var later = new LaterListEntriesOptions { Known = DdbListEntriesOptions.Create(true, false, false) };
        later.Known.StructSize = (uint)sizeof(LaterListEntriesOptions);
        Ok(NativeDdbAbi11.DDBListEntriesEx(root, "", "", "", in later.Known, out var laterOutput),
            "DDBListEntriesEx(structSize 16, unknown fields zero)");
        ShouldEqualJson(TakeJson(laterOutput), withoutBuild);
        later.Unknown = 1;
        Fails(NativeDdbAbi11.DDBListEntriesEx(root, "", "", "", in later.Known, out _), DdbResult.InvalidArgs,
            "DDBListEntriesEx(structSize 16, a non-zero unknown field)");
        Fails(NativeDdbAbi11.DDBListEntriesEx(root, "", "", "nonsense", DdbListEntriesOptions.Create(true, false, false),
            out _), DdbResult.InvalidArgs, "DDBListEntriesEx(unknown type)");
        Fails(NativeDdbAbi11.DDBListEntriesEx("", "", "", "", DdbListEntriesOptions.Create(true, false, false), out _),
            DdbResult.InvalidArgs, "DDBListEntriesEx(empty ddbPath)");
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

        Ok(NativeDdbAbi11.DDBConfigure(new JObject
        {
            ["cacheDir"] = cache, ["cpuPoolSize"] = 3, ["ioThreads"] = 2, ["minFreeDiskMb"] = 256
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
                  "settings": {"cacheDir": "<cache>", "cpuPoolSize": 3, "ioThreads": 2, "minFreeDiskMb": 256},
                  "sources": {"cacheDir": "configure", "cpuPoolSize": "configure", "ioThreads": "configure",
                              "minFreeDiskMb": "configure"}
                }
                """));

            var before = EffectiveConfig();
            foreach (var invalid in new[]
                     {
                         null, "[]", "not json", """{"cpuPoolSizee": 1}""", """{"ioThreads": 0}""",
                         """{"cpuPoolSize": 1, "minFreeDiskMb": -1}""", """{"cacheDir": "relative/folder"}"""
                     })
                Fails(NativeDdbAbi11.DDBConfigure(invalid), DdbResult.InvalidArgs, $"DDBConfigure({invalid ?? "NULL"})");
            ShouldEqualJson(EffectiveConfig(), before, "an invalid DDBConfigure changes nothing");
        }
        finally
        {
            Ok(NativeDdbAbi11.DDBConfigure(
                """{"cacheDir": null, "cpuPoolSize": null, "ioThreads": null, "minFreeDiskMb": null}"""),
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

        Ok(NativeDdbAbi11.DDBCreateCancelToken(out var token), "DDBCreateCancelToken");
        token.ShouldNotBe(IntPtr.Zero);
        try
        {
            // A live token: the call works
            Ok(NativeDdbAbi11.DDBSetThreadCancelToken(token), "DDBSetThreadCancelToken");
            DdbWrapper.Build(root, GeoRasterName, force: true);

            Ok(NativeDdbAbi11.DDBCancel(token), "DDBCancel");
            Ok(NativeDdbAbi11.DDBCancel(token), "DDBCancel(again)");

            // DDBBuild is "cancellable" (capabilities); NativeDdbWrapper.Build calls it first on this thread
            Ok(NativeDdbAbi11.DDBSetThreadCancelToken(token), "DDBSetThreadCancelToken");
            Should.Throw<DdbCanceledException>(() => DdbWrapper.Build(root, GeoRasterName, force: true))
                .Message.ShouldContain("Canceled by the cancel token");

            // The canceled call reset the thread's token: the next call works
            DdbWrapper.Build(root, GeoRasterName, force: true);
        }
        finally
        {
            Ok(NativeDdbAbi11.DDBFreeCancelToken(token), "DDBFreeCancelToken");
        }

        Fails(NativeDdbAbi11.DDBCancel(IntPtr.Zero), DdbResult.InvalidArgs, "DDBCancel(NULL)");
        Ok(NativeDdbAbi11.DDBFreeCancelToken(IntPtr.Zero), "DDBFreeCancelToken(NULL)");
        Ok(NativeDdbAbi11.DDBSetThreadCancelToken(IntPtr.Zero), "DDBSetThreadCancelToken(NULL)");
    }

    [Test]
    public void SetLogCallback_ReceivesGdalErrorsWithTheOperationIdOfTheCall()
    {
        var log = new LogCollector();
        var handle = GCHandle.Alloc(log);
        Ok(NativeDdbAbi11.DDBSetLogCallback(&OnLog, (int)DdbLogLevel.Warning, GCHandle.ToIntPtr(handle)),
            "DDBSetLogCallback");
        try
        {
            using var area = new TestArea(nameof(SetLogCallback_ReceivesGdalErrorsWithTheOperationIdOfTheCall));
            var notARaster = Path.Combine(area.TestFolder, "not-a-raster.tif");
            File.WriteAllText(notARaster, "not a raster");

            Ok(NativeDdbAbi11.DDBSetThreadOperation(42), "DDBSetThreadOperation");
            Should.Throw<DdbException>(() => DdbWrapper.GetRasterInfo(notARaster));
            // The operation id was for that call only
            Should.Throw<DdbException>(() => DdbWrapper.GetRasterInfo(notARaster));
        }
        finally
        {
            // Returns once no thread is inside the callback any more
            Ok(NativeDdbAbi11.DDBSetLogCallback(null, 0, IntPtr.Zero), "DDBSetLogCallback(NULL)");
            handle.Free();
        }

        var records = log.Records;
        records.ShouldNotBeEmpty("GDAL's error on the unreadable raster reaches the callback");
        records.ShouldAllBe(r => r.Level >= (int)DdbLogLevel.Warning && r.Level <= (int)DdbLogLevel.Fatal);
        records.ShouldAllBe(r => r.Source == "gdal" || r.Source == "ddb");
        string.Join(",", records.Select(r => r.OpId).Distinct().Order())
            .ShouldBe("0,42", "the first call logs with operation 42, the second with none");

        Fails(NativeDdbAbi11.DDBSetLogCallback(&OnLog, (int)DdbLogLevel.Fatal + 1, IntPtr.Zero), DdbResult.InvalidArgs,
            "DDBSetLogCallback(minLevel 6)");
        Fails(NativeDdbAbi11.DDBSetLogCallback(&OnLog, -1, IntPtr.Zero), DdbResult.InvalidArgs,
            "DDBSetLogCallback(minLevel -1)");
    }

    /// <summary>
    /// DDBShutdown is process-final, so <see cref="Abi11ShutdownProbe"/> runs it in a child
    /// process (this test assembly started with its probe argument) and reports each step.
    /// </summary>
    [Test]
    public void Shutdown_RunsInAChildProcess_RefusesLaterCalls_AndKeepsTheExemptOnes()
    {
        var assembly = typeof(Abi11ShutdownProbe).Assembly.Location;
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
            ArgumentList = { "exec", assembly, Abi11ShutdownProbe.Argument }
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
        stdout.Result.ShouldContain(Abi11ShutdownProbe.Passed, Case.Sensitive, output);
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

        var delta = DdbWrapper.Delta(source, destination);
        delta.Adds.Select(a => (a.Path, a.Hash)).ToArray().ShouldBe(new[] { (TextName, TextHash) });
        var deltaJson = JsonConvert.SerializeObject(delta);

        foreach (var invalid in new[]
                 {
                     "not json",
                     "[\"a.txt\"]",
                     $$"""{"a.txt": "{{Sha256("other")}}"}""", // not the delta's hash
                     $$"""{"a.txt": "{{TextHash.ToUpperInvariant()}}"}""", // exactly the delta's hash only
                     $$"""{"a.txt": "{{TextHash}}", "b.txt": "{{TextHash}}"}""" // b.txt is not in the delta
                 })
        {
            Fails(NativeDdbAbi11.DDBApplyDeltaEx(deltaJson, source, destination, (int)MergeStrategy.KeepTheirs, "[]",
                invalid, out _), DdbResult.InvalidArgs, $"DDBApplyDeltaEx({invalid})");
            File.Exists(Path.Combine(source, TextName)).ShouldBeTrue("an invalid call changes nothing");
            File.Exists(Path.Combine(destination, TextName)).ShouldBeFalse("an invalid call changes nothing");
        }

        Fails(NativeDdbAbi11.DDBApplyDeltaEx(null, source, destination, (int)MergeStrategy.KeepTheirs, "[]", null, out _),
            DdbResult.InvalidArgs, "DDBApplyDeltaEx(NULL delta)");

        Ok(NativeDdbAbi11.DDBApplyDeltaEx(deltaJson, source, destination, (int)MergeStrategy.KeepTheirs, "[]",
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
        var ptr = NativeDdbAbi11.DDBGetCapabilities();
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
        Ok(NativeDdbAbi11.DDBGetEffectiveConfig(out var output), "DDBGetEffectiveConfig");
        return (JObject)TakeJson(output);
    }

    private static JArray GetBuildArtifacts(string root, params string[] paths)
    {
        Ok(NativeDdbAbi11.DDBGetBuildArtifacts(root, paths, paths.Length, out var output), "DDBGetBuildArtifacts");
        var artifacts = (JArray)TakeJson(output);
        artifacts.Count.ShouldBe(paths.Length, "one element per input path");
        return artifacts;
    }

    private static JArray ListEntriesEx(string root, DdbListEntriesOptions options)
    {
        Ok(NativeDdbAbi11.DDBListEntriesEx(root, "", "", "", options, out var output), "DDBListEntriesEx");
        return (JArray)TakeJson(output);
    }

    /// <summary>
    /// A dataset folder <paramref name="name"/> with the contract's a.txt and, optionally, the
    /// GeoRaster, both with the contract mtime, indexed.
    /// </summary>
    private string CreateDataset(TestArea area, string name, bool withGeoRaster)
    {
        var root = Path.Combine(area.TestFolder, name);
        Directory.CreateDirectory(root);
        DdbWrapper.Init(root);

        var files = new List<string> { WriteContractFile(root, TextName, TextContent) };
        if (withGeoRaster)
        {
            var raster = Path.Combine(root, GeoRasterName);
            File.Copy(_geoRaster!, raster);
            File.SetLastWriteTimeUtc(raster, ContractMtime);
            files.Add(raster);
        }

        DdbWrapper.Add(root, files.ToArray());
        return root;
    }

    private static string WriteContractFile(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, ContractMtime);
        return path;
    }

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

    /// <summary>A DDBListEntriesOptions of a later ABI: the ABI 1.1 struct and a field appended after it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LaterListEntriesOptions
    {
        public DdbListEntriesOptions Known;
        public ulong Unknown;
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
