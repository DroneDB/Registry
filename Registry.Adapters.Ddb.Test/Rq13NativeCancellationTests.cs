#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.Abi11Native;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// RQ-13 (XI-07): the call envelope hands the current <see cref="DdbCancellation"/> to libddb as a cancel
/// token, for the exports that <c>DDBGetCapabilities().cancellable</c> lists, on the real library. A canceled
/// token stops the call with <c>DDBERR_CANCELED</c> (<see cref="DdbCanceledException"/>, an
/// <see cref="OperationCanceledException"/> carrying the managed token), for that call only: the next call on
/// the same thread runs. No delays: the mid-call case cancels from the export's own progress report, after
/// which libddb checks the token before it goes on.
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~Rq13NativeCancellationTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class Rq13NativeCancellationTests : TestBase
{
    private const string BaseTestFolder = nameof(Rq13NativeCancellationTests);
    private const string CanceledByToken = "Canceled by the cancel token";
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
    public void Capabilities_ListTheExportsTheEnvelopeCancels()
    {
        var capabilities = DdbAbi.Verify();

        foreach (var export in new[]
                 {
                     nameof(NativeDdb.DDBBuild), nameof(NativeDdb.DDBExportRaster), nameof(NativeDdb.DDBExportRaster2),
                     nameof(NativeDdb.DDBGenerateMemoryThumbnail), nameof(NativeDdb.DDBGenerateMemoryThumbnailEx),
                     nameof(NativeDdb.DDBGenerateThumbnail), nameof(NativeDdb.DDBMemoryTile),
                     nameof(NativeDdb.DDBMemoryTileEx), nameof(NativeDdb.DDBMemoryTileFmt),
                     nameof(NativeDdb.DDBRenderRasterIndex), nameof(NativeDdb.DDBRenderRasterRegionEx),
                     nameof(NativeDdb.DDBTile)
                 })
            capabilities.IsCancellable(export).ShouldBeTrue(export);

        capabilities.IsCancellable(nameof(NativeDdb.DDBGetRasterInfo)).ShouldBeFalse();
    }

    [Test]
    public void PreCanceledToken_StopsACancellableExport_ForThatCallOnly()
    {
        using var area = new TestArea(nameof(PreCanceledToken_StopsACancellableExport_ForThatCallOnly));
        var raster = Path.Combine(CreateDataset(Wrapper, area, "dataset", _geoRaster), GeoRasterName);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        using (DdbCancellation.Enter(canceled.Token))
        {
            var ex = Should.Throw<DdbCanceledException>(() => Wrapper.GenerateThumbnail(raster, 64));
            ex.ShouldBeAssignableTo<OperationCanceledException>();
            ex.CancellationToken.ShouldBe(canceled.Token);
            ex.Message.ShouldContain(CanceledByToken);

            // An export libddb does not list as cancellable gets no token: it runs
            Wrapper.GetRasterInfo(raster).ShouldNotBeNullOrWhiteSpace();
        }

        // Same thread, outside the scope: the canceled call took its token with it
        Wrapper.GenerateThumbnail(raster, 64).ShouldNotBeEmpty();
    }

    [Test]
    public void CanceledToken_StopsDDBBuild_AndTheNextBuildRuns()
    {
        using var area = new TestArea(nameof(CanceledToken_StopsDDBBuild_AndTheNextBuildRuns));
        var root = CreateDataset(Wrapper, area, "dataset", _geoRaster);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        using (DdbCancellation.Enter(canceled.Token))
            Should.Throw<DdbCanceledException>(() => Wrapper.Build(root, GeoRasterName, force: true))
                .Message.ShouldContain(CanceledByToken);

        Wrapper.GetBuildArtifacts(root, [GeoRasterName]).ShouldHaveSingleItem().Build!.State
            .ShouldNotBe(BuildState.Complete, "a canceled build leaves no artifact");

        Wrapper.Build(root, GeoRasterName, force: true);
        Wrapper.GetBuildArtifacts(root, [GeoRasterName]).ShouldHaveSingleItem().Build!.State
            .ShouldBe(BuildState.Complete);
    }

    [Test]
    public void TokenCanceledWhileTheCallRuns_StopsItAtItsNextCheckpoint()
    {
        using var area = new TestArea(nameof(TokenCanceledWhileTheCallRuns_StopsItAtItsNextCheckpoint));
        var raster = Path.Combine(CreateDataset(Wrapper, area, "dataset", _geoRaster), GeoRasterName);
        var output = Path.Combine(area.TestFolder, "export.tif");
        using var request = new CancellationTokenSource();
        var reports = 0;

        using (DdbCancellation.Enter(request.Token))
        {
            // The export's own token is none, so its progress callback never asks it to stop: the first report
            // cancels the request's token (DDBCancel from inside the call), and libddb checks it right after
            var ex = Should.Throw<DdbCanceledException>(() => Wrapper.ExportRaster(raster, output, null, null, null,
                null, null, null, 0, (_, _) =>
                {
                    reports++;
                    request.Cancel();
                }, CancellationToken.None));

            ex.Message.ShouldContain(CanceledByToken);
            ex.CancellationToken.ShouldBe(request.Token);
        }

        reports.ShouldBe(1, "the export stopped at the checkpoint after the first report");
        File.Exists(output).ShouldBeFalse("a canceled export deletes its partial output");
    }

    [Test]
    public void LiveToken_TheCallKeepsItsOperationId()
    {
        using var area = new TestArea(nameof(LiveToken_TheCallKeepsItsOperationId));
        var notARaster = Path.Combine(area.TestFolder, "not-a-raster.tif");
        File.WriteAllText(notARaster, "not a raster");
        var sink = new OperationSink();
        using var live = new CancellationTokenSource();

        Runtime.SetLogSink(sink, DdbLogLevel.Warning);
        try
        {
            // Creating a native token is an operation call, which resets the thread's operation id: the
            // envelope creates it before it sets the id, so the export still logs with it
            using (DdbOperation.Enter(4242))
            using (DdbCancellation.Enter(live.Token))
                Should.Throw<DdbException>(() => Wrapper.GenerateThumbnail(notARaster, 64));

            sink.Records.ShouldNotBeEmpty();
            sink.Records.ShouldAllBe(id => id == 4242UL);
        }
        finally
        {
            Runtime.SetLogSink(null, DdbLogLevel.Info);
        }
    }

    private sealed class OperationSink : IDdbLogSink
    {
        public ConcurrentQueue<ulong> Records { get; } = new();

        public void Write(DdbLogLevel level, string message, string source, ulong operationId) =>
            Records.Enqueue(operationId);
    }
}
