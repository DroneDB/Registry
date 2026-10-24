#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// RQ-08 on the real libddb (ABI 2.0): the startup handshake, the new port members
/// (<see cref="NativeDdbWrapper.GetBuildArtifacts"/>, <see cref="NativeDdbWrapper.ResolveBuildFile"/>,
/// <see cref="NativeDdbWrapper.GetEntryTypes"/>, <see cref="NativeDdbWrapper.CopyBuildArtifacts"/>), the
/// error contract through the adapter (each code its typed exception, with the failing call's own
/// message and reason, XI-02) and the runtime's log sink (XI-08). The raw exports are covered by
/// <see cref="LibddbContractTests"/>.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~Rq08NativeAdapterTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class Rq08NativeAdapterTests : TestBase
{
    private const string BaseTestFolder = nameof(Rq08NativeAdapterTests);
    private static readonly string GeoRasterUrl = TestData.Url("testdata/raster/byte.tif");

    private static readonly NativeDdbWrapper Wrapper = new();
    private static readonly NativeDdbRuntime Runtime = new();

    private string _geoRaster = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        Runtime.RegisterProcess();
        _geoRaster = TestDownloadCache.GetOrDownload(GeoRasterUrl, Path.Combine(Path.GetTempPath(), BaseTestFolder));
    }

    [OneTimeTearDown]
    public void DeleteDownloads() => TestDownloadCache.Evict(_geoRaster);

    [Test]
    public void Handshake_TheLoadedLibraryIsLibddb2_AndExportsEveryImport()
    {
        var capabilities = DdbAbi.Verify();

        capabilities.AbiMajor.ShouldBe(DdbAbi.Major);
        capabilities.AbiMinor.ShouldBeGreaterThanOrEqualTo(DdbAbi.Minor);
        DdbAbi.RequiredSymbols.Where(s => !capabilities.Exports(s)).ShouldBeEmpty();
        Runtime.Capabilities.ShouldBeSameAs(capabilities);
    }

    [Test]
    public void BuildArtifacts_ResolveBuildFile_OfABuiltGeoRaster()
    {
        using var area = new TestArea(nameof(BuildArtifacts_ResolveBuildFile_OfABuiltGeoRaster));
        var root = CreateDataset(Wrapper, area, "dataset", _geoRaster);
        Wrapper.Build(root, GeoRasterName);

        var built = Wrapper.GetBuildArtifacts(root, [GeoRasterName]).ShouldHaveSingleItem();
        built.Build!.State.ShouldBe(BuildState.Complete);
        var cog = built.Build.Artifacts.Single(a => a.Role == ArtifactRoles.RasterCog);

        var file = Wrapper.ResolveBuildFile(root, built.Build.Hash!, cog.Path);
        File.Exists(file).ShouldBeTrue(file);

        // NOTFOUND is the absent answer, INVALIDARGS a typed client error with libddb's message
        Wrapper.ResolveBuildFile(root, new string('0', 64), cog.Path).ShouldBeNull();
        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.ResolveBuildFile(root, "not-a-hash", cog.Path))
            .Message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.ResolveBuildFile(root, built.Build.Hash!, "../../dbase.sqlite"));
    }

    [Test]
    public void CopyBuildArtifacts_CopiesTheBuild_AndMapsItsErrors()
    {
        using var area = new TestArea(nameof(CopyBuildArtifacts_CopiesTheBuild_AndMapsItsErrors));
        var source = CreateDataset(Wrapper, area, "source", _geoRaster);
        var destination = CreateDataset(Wrapper, area, "destination", _geoRaster);
        Wrapper.Build(source, GeoRasterName);
        var hash = Wrapper.GetBuildArtifacts(source, [GeoRasterName]).Single().Build!.Hash!;

        var copied = Wrapper.CopyBuildArtifacts(source, destination, hash);

        copied.Hash.ShouldBe(hash);
        copied.Artifacts.Select(a => a.Role).ShouldContain(ArtifactRoles.RasterCog);
        Wrapper.GetBuildArtifacts(destination, [GeoRasterName]).Single().Build!.State.ShouldBe(BuildState.Complete);

        Should.Throw<DdbNotFoundException>(() => Wrapper.CopyBuildArtifacts(source, destination, new string('0', 64)));
        Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.CopyBuildArtifacts(source, source, hash));
    }

    /// <summary>XI-02: a full disk is a retryable failure with its reason, not a content failure.</summary>
    [Test]
    public void CopyBuildArtifacts_BelowTheDiskBudget_IsRetryable_WithTheReason()
    {
        using var area = new TestArea(nameof(CopyBuildArtifacts_BelowTheDiskBudget_IsRetryable_WithTheReason));
        var source = CreateDataset(Wrapper, area, "source", _geoRaster);
        var destination = CreateDataset(Wrapper, area, "destination", _geoRaster);
        Wrapper.Build(source, GeoRasterName);
        var hash = Wrapper.GetBuildArtifacts(source, [GeoRasterName]).Single().Build!.Hash!;

        Runtime.Configure(new DdbProcessSettings(MinFreeDiskMb: 10_000_000_000_000));
        try
        {
            Should.Throw<DdbRetryableException>(() => Wrapper.CopyBuildArtifacts(source, destination, hash))
                .Reason.ShouldBe(DdbRetryableException.Reasons.LowDiskSpace);
        }
        finally
        {
            Ok(NativeDdb.DDBConfigure("""{"minFreeDiskMb": null}"""), "DDBConfigure(reset)");
        }
    }

    [Test]
    public void EntryTypes_AreLibddbs()
    {
        var types = Wrapper.GetEntryTypes();

        types.Select(t => t.Id).ShouldBe(Enum.GetValues<EntryType>());
        types.Single(t => t.Id == EntryType.GeoRaster).Roles.ShouldContain(ArtifactRoles.RasterCog);
        Wrapper.GetEntryTypes().ShouldBeSameAs(types, "the types never change in a process");
    }

    // The one rule of the thumbnail endpoints (a type without a thumbnail is a 404, not a render): libddb's
    [Test]
    public void EntryTypes_SayWhichHaveAThumbnail()
    {
        var withThumbnail = Wrapper.GetEntryTypes().Where(t => t.HasThumbnail).Select(t => t.Id);

        withThumbnail.ShouldBe(
            [EntryType.GeoImage, EntryType.GeoRaster, EntryType.PointCloud, EntryType.Image, EntryType.Panorama,
                EntryType.GeoPanorama, EntryType.GaussianSplat],
            ignoreOrder: true);
        Wrapper.GetEntryTypes().Single(t => t.Id == EntryType.Model).Thumbnail.ShouldBe("none");
        Wrapper.GetEntryTypes().Single(t => t.Id == EntryType.Video).Thumbnail.ShouldBe("none");
    }

    /// <summary>XI-02: the message of a failure is the failing call's own, never a stale one of an earlier call.</summary>
    [Test]
    public void LastError_IsTheFailingCallsOwn()
    {
        using var area = new TestArea(nameof(LastError_IsTheFailingCallsOwn));
        var root = CreateDataset(Wrapper, area, "dataset", null);
        var missing = Path.Combine(area.TestFolder, "no-dataset-here");

        var invalid = Should.Throw<DdbInvalidArgumentsException>(() => Wrapper.ResolveBuildFile(root, "not-a-hash", "x"));
        Wrapper.GetStamp(root).ShouldNotBeNull();
        DdbResultMapper.GetNativeLastError().ShouldBe("", "a successful call clears the last error");
        var notADataset = Should.Throw<DdbException>(() => Wrapper.GetStamp(missing));

        invalid.Message.ShouldNotBeNullOrWhiteSpace();
        notADataset.Message.ShouldNotBeNullOrWhiteSpace();
        notADataset.Message.ShouldNotBe(invalid.Message);
    }

    /// <summary>XI-08: native records reach the sink; a sink that throws never ends the process.</summary>
    [Test]
    public void LogSink_ReceivesNativeRecords_AndASinkFailureIsDropped()
    {
        using var area = new TestArea(nameof(LogSink_ReceivesNativeRecords_AndASinkFailureIsDropped));
        var notARaster = Path.Combine(area.TestFolder, "not-a-raster.tif");
        File.WriteAllText(notARaster, "not a raster");
        var sink = new CollectingSink();

        Runtime.SetLogSink(sink, DdbLogLevel.Warning);
        try
        {
            Should.Throw<DdbException>(() => Wrapper.GetRasterInfo(notARaster));
            sink.Records.ShouldNotBeEmpty();
            sink.Records.ShouldAllBe(r => r.Level >= DdbLogLevel.Warning);

            Runtime.SetLogSink(new ThrowingSink(), DdbLogLevel.Warning);
            Should.Throw<DdbException>(() => Wrapper.GetRasterInfo(notARaster));
            Wrapper.GetVersion().ShouldNotBeNullOrWhiteSpace("the process survived the sink's exception");
        }
        finally
        {
            Runtime.SetLogSink(null, DdbLogLevel.Info);
        }
    }

    private sealed class CollectingSink : IDdbLogSink
    {
        public ConcurrentQueue<(DdbLogLevel Level, string Message, string Source)> Records { get; } = new();

        public void Write(DdbLogLevel level, string message, string source, ulong operationId) =>
            Records.Enqueue((level, message, source));
    }

    private sealed class ThrowingSink : IDdbLogSink
    {
        public void Write(DdbLogLevel level, string message, string source, ulong operationId) =>
            throw new InvalidOperationException("sink failure");
    }
}
