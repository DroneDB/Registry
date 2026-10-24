#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Shouldly;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// XI-07 steps 3 and 4 on the real libddb (2c9508a: 13 more cancellable exports, 26 in all): the raster analysis exports,
/// the volume, stockpile and contour exports, <c>DDBMaskBorders</c> and the delta exports run under the cancel token the
/// call envelope gives every export libddb lists, so a canceled request stops them. Nothing here is Registry code of its
/// own: the envelope picks the new exports up from <c>DDBGetCapabilities().cancellable</c>, and these tests show it does.
/// <see cref="DdbCancellation"/> is entered right around each call, as the native work gate and the push commit do.
/// <para>
/// <c>DDBApplyDeltaEx</c> is atomic to a cancel: canceled before it changes anything it returns
/// <c>DDBERR_CANCELED</c> with the dataset, the index and the source files as they were; from its first change on it
/// applies the delta whole and returns <c>DDBERR_NONE</c>, whatever the token says. The late cancel is made without
/// timing: the token is canceled by the log record libddb writes when the part that changes the dataset starts.
/// </para>
/// Run: dotnet test Registry.Adapters.Ddb.Test --filter "FullyQualifiedName~Xi07CancellationStepsTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Native)]
[Category(TestCategories.Network)]
public class Xi07CancellationStepsTests : TestBase
{
    private const string BaseTestFolder = nameof(Xi07CancellationStepsTests);
    private const string CanceledByToken = "Canceled by the cancel token";
    private static readonly string RasterUrl = TestData.Url("brighton/odm_orthophoto.tif");

    // Inside the Brighton ortho (see DroneDBTests: CalculateVolume_HappyPath_Ok)
    private const string Polygon = "{\"type\":\"Polygon\",\"coordinates\":[[[-91.99400,46.84240],[-91.99360,46.84240]," +
                                   "[-91.99360,46.84270],[-91.99400,46.84270],[-91.99400,46.84240]]]}";

    private const string Line = "{\"type\":\"LineString\",\"coordinates\":[[-91.99400,46.84240],[-91.99360,46.84270]]}";

    private static readonly NativeDdbWrapper Wrapper = new();
    private static readonly NativeDdbRuntime Runtime = new();

    private static readonly string[] NewlyCancellable =
    [
        nameof(NativeDdb.DDBGetRasterMetadata), nameof(NativeDdb.DDBGetRasterValueInfo),
        nameof(NativeDdb.DDBGetRasterPointValue), nameof(NativeDdb.DDBGetRasterAreaStats),
        nameof(NativeDdb.DDBGetRasterProfile), nameof(NativeDdb.DDBCalculateVolume),
        nameof(NativeDdb.DDBDetectStockpile), nameof(NativeDdb.DDBDetectAllStockpiles),
        nameof(NativeDdb.DDBGenerateContours), nameof(NativeDdb.DDBMaskBorders),
        nameof(NativeDdb.DDBComputeDeltaLocals), nameof(NativeDdb.DDBApplyDeltaEx)
    ];

    private string _raster = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        UseDdbFolder();
        var (gate, message) = CheckAbi();
        if (gate != Gate.Applies)
            Assert.Fail(message);

        Runtime.RegisterProcess();
        _raster = TestDownloadCache.GetOrDownload(RasterUrl, Path.Combine(Path.GetTempPath(), BaseTestFolder));
    }

    [OneTimeTearDown]
    public void DeleteDownloads() => TestDownloadCache.Evict(_raster);

    [Test]
    public void Capabilities_ListTheRasterAnalysisAndDeltaExports()
    {
        var capabilities = DdbAbi.Verify();

        foreach (var export in NewlyCancellable)
            capabilities.IsCancellable(export).ShouldBeTrue(export);
        capabilities.Cancellable.Count.ShouldBeGreaterThanOrEqualTo(26);
    }

    /// <summary>
    /// A canceled token stops each of the raster exports at once, for that call only: with no token the next call
    /// of the same export is not canceled (it answers or fails on its own terms).
    /// </summary>
    [Test]
    public void PreCanceledToken_StopsEachRasterExport_ForThatCallOnly()
    {
        using var area = new TestArea(nameof(PreCanceledToken_StopsEachRasterExport_ForThatCallOnly));
        var masked = Path.Combine(area.TestFolder, "masked.tif");

        var calls = new Dictionary<string, Action>
        {
            [nameof(NativeDdb.DDBGetRasterMetadata)] = () => Wrapper.GetRasterMetadata(_raster),
            [nameof(NativeDdb.DDBGetRasterValueInfo)] = () => Wrapper.GetRasterValueInfo(_raster),
            [nameof(NativeDdb.DDBGetRasterPointValue)] = () => Wrapper.GetRasterPointValue(_raster, 10, 10),
            [nameof(NativeDdb.DDBGetRasterAreaStats)] = () => Wrapper.GetRasterAreaStats(_raster, 0, 0, 20, 20),
            [nameof(NativeDdb.DDBGetRasterProfile)] = () => Wrapper.GetRasterProfile(_raster, Line, 16),
            [nameof(NativeDdb.DDBCalculateVolume)] = () => Wrapper.CalculateVolume(_raster, Polygon, "lowest_perimeter", 0.0),
            [nameof(NativeDdb.DDBDetectStockpile)] = () => Wrapper.DetectStockpile(_raster, 46.84258, -91.99383, 15.0, 0.5f),
            [nameof(NativeDdb.DDBDetectAllStockpiles)] = () => Wrapper.DetectAllStockpiles(_raster, 0.5f, 10.0, 10),
            [nameof(NativeDdb.DDBGenerateContours)] = () => Wrapper.GenerateContours(_raster, 5.0, null),
            [nameof(NativeDdb.DDBMaskBorders)] = () => Wrapper.MaskBorders(_raster, masked)
        };
        calls.Keys.ShouldBe(NewlyCancellable.Where(e => e != nameof(NativeDdb.DDBComputeDeltaLocals) &&
                                                       !e.StartsWith("DDBApplyDelta", StringComparison.Ordinal)),
            ignoreOrder: true, "every raster export of the step is tried");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        foreach (var (export, call) in calls)
        {
            using (DdbCancellation.Enter(canceled.Token))
            {
                var ex = Should.Throw<DdbCanceledException>(call, export);
                ex.ShouldBeAssignableTo<OperationCanceledException>(export);
                ex.CancellationToken.ShouldBe(canceled.Token, export);
                ex.Message.ShouldContain(CanceledByToken, customMessage: export);
            }

            // The token was for that call only
            try
            {
                call();
            }
            catch (DdbCanceledException)
            {
                Assert.Fail($"{export}: the next call, without a token, was canceled");
            }
            catch (DdbException)
            {
                // a failure of its own (a raster that is not a DEM): not a cancel
            }
        }

        File.Delete(masked);
    }

    [Test]
    public void PreCanceledToken_StopsMaskBorders_AndLeavesNoOutput()
    {
        using var area = new TestArea(nameof(PreCanceledToken_StopsMaskBorders_AndLeavesNoOutput));
        var output = Path.Combine(area.TestFolder, "masked.tif");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        using (DdbCancellation.Enter(canceled.Token))
            Should.Throw<DdbCanceledException>(() => Wrapper.MaskBorders(_raster, output));

        File.Exists(output).ShouldBeFalse("a canceled mask leaves no output");
        Wrapper.MaskBorders(_raster, output);
        File.Exists(output).ShouldBeTrue("the next call, without a token, runs");
    }

    [Test]
    public void ComputeDeltaLocals_PreCanceled_IsCanceled_AndWorksWithoutAToken()
    {
        using var area = new TestArea(nameof(ComputeDeltaLocals_PreCanceled_IsCanceled_AndWorksWithoutAToken));
        var delta = new DeltaCase(area, 5);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        using (DdbCancellation.Enter(canceled.Token))
            Should.Throw<DdbCanceledException>(() => Wrapper.ComputeDeltaLocals(delta.Delta, delta.Root))
                .Message.ShouldContain(CanceledByToken);

        Wrapper.ComputeDeltaLocals(delta.Delta, delta.Root).ShouldNotBeNull();
    }

    /// <summary>Before the delta changes anything a cancel is an error and nothing changed: the same call then applies it.</summary>
    [Test]
    public void ApplyDelta_CanceledBeforeItChangesAnything_ChangesNothing()
    {
        using var area = new TestArea(nameof(ApplyDelta_CanceledBeforeItChangesAnything_ChangesNothing));
        var delta = new DeltaCase(area, 20);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        using (DdbCancellation.Enter(canceled.Token))
        {
            var ex = Should.Throw<DdbCanceledException>(() => delta.Apply());
            ex.CancellationToken.ShouldBe(canceled.Token);
            ex.Message.ShouldContain(CanceledByToken);
        }

        delta.ShouldNotBeApplied();

        delta.Apply().ShouldBeEmpty();
        delta.ShouldBeApplied();
    }

    /// <summary>
    /// The token is canceled at the first record of the part that changes the dataset: the delta is applied whole and the
    /// call returns normally (a cancel that came too late is not an error), with the managed token canceled.
    /// </summary>
    [Test]
    public void ApplyDelta_CanceledOnceItChangesTheDataset_IsAppliedWhole()
    {
        using var area = new TestArea(nameof(ApplyDelta_CanceledOnceItChangesTheDataset_IsAppliedWhole));
        var delta = new DeltaCase(area, 60);
        using var request = new CancellationTokenSource();
        var sink = new CancelOnRecord("Applying a delta", request);

        Runtime.SetLogSink(sink, DdbLogLevel.Debug);
        try
        {
            using (DdbCancellation.Enter(request.Token))
                delta.Apply().ShouldBeEmpty();
        }
        finally
        {
            Runtime.SetLogSink(null, DdbLogLevel.Info);
        }

        sink.Fired.ShouldBeTrue("libddb logged the start of the apply: the test checks something");
        request.IsCancellationRequested.ShouldBeTrue();
        delta.ShouldBeApplied();
    }

    private sealed class CancelOnRecord(string trigger, CancellationTokenSource source) : IDdbLogSink
    {
        public bool Fired { get; private set; }

        public void Write(DdbLogLevel level, string message, string logSource, ulong operationId)
        {
            if (Fired || !message.Contains(trigger, StringComparison.Ordinal))
                return;

            Fired = true;
            source.Cancel();
        }
    }

    /// <summary>A dataset and a delta that adds a folder and <c>files</c> files, waiting in a push-like folder.</summary>
    private sealed class DeltaCase
    {
        private readonly string _addFolder;
        private readonly string[] _paths;

        public DeltaCase(TestArea area, int files)
        {
            Root = Path.Combine(area.TestFolder, "dataset");
            Directory.CreateDirectory(Root);
            Wrapper.Init(Root);
            _addFolder = Path.Combine(Root, ".ddb", "tmp", "push-test", "add");

            var adds = new List<AddAction> { new("folder", "") };
            var paths = new List<string>();
            var hashes = new Dictionary<string, string>();
            for (var i = 0; i < files; i++)
            {
                var path = $"folder/file-{i}.txt";
                var content = $"content {i}";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_addFolder, path))!);
                File.WriteAllText(Path.Combine(_addFolder, path), content);
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
                adds.Add(new AddAction(path, hash));
                paths.Add(path);
                hashes[path] = hash;
            }

            _paths = [.. paths];
            Delta = new Delta { Adds = [.. adds], Removes = [], MetaAdds = [], MetaRemoves = [] };
            Verified = hashes;
        }

        public string Root { get; }

        public Delta Delta { get; }

        private IReadOnlyDictionary<string, string> Verified { get; }

        public List<string> Apply() =>
            Wrapper.ApplyDelta(Delta, _addFolder, Root, MergeStrategy.KeepTheirs, null, Verified);

        public void ShouldNotBeApplied()
        {
            foreach (var path in _paths)
            {
                File.Exists(Path.Combine(Root, path)).ShouldBeFalse(path);
                File.Exists(Path.Combine(_addFolder, path)).ShouldBeTrue(path);
            }

            Wrapper.GetEntry(Root, _paths[0]).ShouldBeNull("the index is as it was");
        }

        public void ShouldBeApplied()
        {
            foreach (var path in _paths)
            {
                File.Exists(Path.Combine(Root, path)).ShouldBeTrue(path);
                File.Exists(Path.Combine(_addFolder, path)).ShouldBeFalse(path + " is moved, not copied");
                Wrapper.GetEntry(Root, path).ShouldNotBeNull(path);
            }
        }
    }
}
