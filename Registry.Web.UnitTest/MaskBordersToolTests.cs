using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The Mask Borders heavy tool (FR-PRC-12, D-013, FR-XC-25, FR-XC-26, FR-XC-07): it declares its input, its output
/// (<c>&lt;name&gt;_masked.tif</c> next to the source) and its label, sets <c>AddsDatasetFiles</c>, refuses an existing
/// copy without <c>overwrite</c> (409, the copy kept), replaces it with <c>overwrite</c>, records and marks the output, indexes
/// it and queues its build as a child of the task.
/// </summary>
[TestFixture]
public class MaskBordersToolTests
{
    private OutputToolHarness _h = null!;
    private List<IndexPayload> _builds = null!;
    private MaskBordersTool _tool = null!;

    [SetUp]
    public void SetUp()
    {
        _h = new OutputToolHarness();
        _builds = [];
        _h.File("ortho/black.tif");
        _h.Ddb.Setup(d => d.MaskBorders("ortho/black.tif", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Callback<string, string, int, bool, bool>((_, output, near, white, overwrite) =>
            {
                _h.Calls.Add($"mask {output} near={near} white={white}" + (overwrite ? " (replace)" : ""));
                File.WriteAllText(_h.Local(output), "masked");
            });
        // The index of the output answers the batch the indexer sends, and the build manifest says it is buildable
        _h.Ddb.Setup(d => d.AddRawBatch(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> paths) =>
        {
            foreach (var p in paths) _h.Calls.Add("index " + p);
            return [.. paths.Select(p => new Entry { Path = p, Hash = "h-" + p, Type = EntryType.GeoRaster })];
        });
        _h.Ddb.Setup(d => d.GetBuildArtifacts(It.IsAny<IReadOnlyList<string>>())).Returns(
            (IReadOnlyList<string> paths) => paths
                .Select(p => TestBuilds.Build(_h.Folder, p, EntryType.GeoRaster, "h-" + p, BuildState.None)).ToArray());
        var jobs = new Mock<IBackgroundJobsProcessor>();
        jobs.Setup(j => j.EnqueueIndexed(It.IsAny<Expression<Action<DdbJobs>>>(), It.IsAny<IndexPayload>()))
            .Callback<Expression<Action<DdbJobs>>, IndexPayload>((_, meta) =>
            {
                _h.Calls.Add("build " + meta.Path);
                _builds.Add(meta);
            })
            .Returns("build-job");
        _h.Ctx.SetupGet(c => c.BuildQueue).Returns(TestJobs.Scheduler(jobs.Object));
        _tool = new MaskBordersTool(TestPaths.Policy, TestJobs.FileIndexer());
    }

    [TearDown]
    public void TearDown() => _h.Dispose();

    private static HeavyToolRequest Request(object? parameters = null) =>
        new("mask-borders", "1", "org", "ds", null,
            JsonSerializer.SerializeToElement(parameters ?? new { path = "ortho/black.tif" }));

    private static HeavyToolRequest Overwrite(string path = "ortho/black.tif") =>
        Request(new { path, overwrite = true });

    private Task Execute(HeavyToolRequest request) =>
        _tool.ExecuteAsync(request, _h.Ctx.Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

    [Test]
    public void DeclaresItsInputItsOutputAndItsLabel_AndAddsDatasetFiles()
    {
        var request = Request(new { path = "./ortho/black.tif" });

        _tool.Id.ShouldBe("mask-borders");
        _tool.InputPaths(request).ShouldBe(["ortho/black.tif"]);
        _tool.OutputPaths(request).ShouldBe(["ortho/black_masked.tif"]);
        _tool.Label(request).ShouldBe("→ ortho/black_masked.tif");
        _tool.AddsDatasetFiles.ShouldBeTrue("the storage quota gate and the write record apply (FR-XC-26)");
        _tool.RequiredAccess.ShouldBe(HeavyToolPermission.Write);
        _tool.InputSchema.RootElement.GetProperty("properties").GetProperty("overwrite")
            .GetProperty("type").GetString().ShouldBe("boolean");
    }

    [TestCase("black.tif", "black_masked.tif")]
    [TestCase("a/b/Ortho.v2.TIF", "a/b/Ortho.v2_masked.tif")]
    public void MaskedOutputPath_IsTheStemWithTheSuffix_NextToTheSource(string source, string masked) =>
        MaskBordersTool.MaskedOutputPath(source).ShouldBe(masked);

    [Test]
    public async Task ASourceThatIsNotAGeoRaster_Is400()
    {
        _h.Ddb.Setup(d => d.GetEntry("doc.pdf")).Returns(new Entry { Path = "doc.pdf", Type = EntryType.Generic });

        await Should.ThrowAsync<BadRequestException>(() =>
            _tool.ValidateAsync(Request(new { path = "doc.pdf" }), _h.Ctx.Object, CancellationToken.None));
    }

    [Test]
    public async Task AMissingSource_Is404()
    {
        await Should.ThrowAsync<NotFoundException>(() =>
            _tool.ValidateAsync(Request(new { path = "ortho/none.tif" }), _h.Ctx.Object, CancellationToken.None));
    }

    [TestCase(-1)]
    [TestCase(256)]
    public async Task ANearOutsideThePixelRange_IsRefused(int near)
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            _tool.ValidateAsync(Request(new { path = "ortho/black.tif", near }), _h.Ctx.Object, CancellationToken.None));

        ex.Message.ShouldBe("near must be between 0 and 255.");
    }

    [Test]
    public async Task ExistingCopy_WithoutOverwrite_Is409WithItsPath_AndIsLeftIntact()
    {
        _h.File("ortho/Black_Masked.tif");

        var ex = await Should.ThrowAsync<NameClashException>(() =>
            _tool.ValidateAsync(Request(), _h.Ctx.Object, CancellationToken.None));
        await Should.ThrowAsync<NameClashException>(() => Execute(Request()));

        ex.StatusCode.ShouldBe(409);
        ex.ConflictPath.ShouldBe("ortho/Black_Masked.tif", "a name that differs only in letter case is the existing copy (D-040)");
        File.ReadAllText(_h.Local("ortho/Black_Masked.tif")).ShouldBe("original");
        _h.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Overwrite_ReplacesTheCopy_MarksIt_IndexesIt_ThenQueuesItsBuild()
    {
        _h.File("ortho/black_masked.tif");

        await _tool.ValidateAsync(Overwrite(), _h.Ctx.Object, CancellationToken.None);
        await Execute(Overwrite());

        _h.Calls.ShouldBe([
            "mask ortho/black_masked.tif near=15 white=False (replace)", "published ortho/black_masked.tif",
            "index ortho/black_masked.tif", "build ortho/black_masked.tif"]);
        File.ReadAllText(_h.Local("ortho/black_masked.tif")).ShouldBe("masked");
    }

    [Test]
    public async Task NewCopy_IsMasked_WithTheNearAndWhiteOfTheRequest_ThenBuiltAsAChildOfTheTask()
    {
        await Execute(Request(new { path = "ortho/black.tif", near = 40, white = true }));

        _h.Calls.ShouldBe([
            "mask ortho/black_masked.tif near=40 white=True", "published ortho/black_masked.tif",
            "index ortho/black_masked.tif", "build ortho/black_masked.tif"]);
        _builds.Single().ParentJobId.ShouldBe("task-1", "the build is a row under the task, with its own name");
    }

    [Test]
    public async Task NewCopy_IsInTheWriteRecord_BeforeTheNativeCall()
    {
        _h.TrackWrites();

        await Execute(Request());

        _h.Calls.IndexOf("record ortho/black_masked.tif").ShouldBeLessThan(
            _h.Calls.IndexOf("mask ortho/black_masked.tif near=15 white=False"));
        _h.Recorder.SavedPaths.ShouldBe(["ortho/black_masked.tif", "ortho/black_masked.tif.msk"]);
    }

    // FR-XC-26, D-039: a copy that does not fit what the owner has left fails the task with the 507 text, and is neither
    // published nor indexed
    [Test]
    public async Task ACopyThatDoesNotFitTheQuotaOfTheOwner_Fails507_AndIsNeitherPublishedNorIndexed()
    {
        _h.OwnerHas(5); // "masked" is 6 bytes

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => Execute(Request()));

        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 5 B left.");
        _h.Calls.ShouldBe(["mask ortho/black_masked.tif near=15 white=False"]);
    }

    [Test]
    public async Task ALaterAttempt_AfterTheWrite_OnlyIndexesAndBuilds()
    {
        _h.File("ortho/black_masked.tif", "masked");
        _h.PublishedBefore("ortho/black_masked.tif");

        await _tool.ValidateAsync(Request(), _h.Ctx.Object, CancellationToken.None);
        await Execute(Request());

        _h.Calls.ShouldBe(["index ortho/black_masked.tif", "build ortho/black_masked.tif"]);
        _h.Ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
    }

    // FR-XC-27, FR-XC-07: the copy a replace overwrites existed before the task: it is never in the write record, so no
    // rollback deletes it
    [Test]
    public async Task Overwrite_RecordsNothing_InTheWriteRecord()
    {
        _h.File("ortho/black_masked.tif");
        _h.TrackWrites();

        await Execute(Overwrite());

        _h.Recorder.SavedPaths.ShouldBeEmpty();
        _h.Recorder.Paths.ShouldBeEmpty();
        _h.Calls.ShouldNotContain("record ortho/black_masked.tif");
    }

    // FR-XC-26, FR-XC-27: a replace whose growth does not fit fails with the 507 text; the replaced copy stays on disk, is
    // indexed again (the entry describes the file) and is not in the write record; no build is queued for it
    [Test]
    public async Task Overwrite_WhoseGrowthDoesNotFit_Fails507_IndexesTheCopyAgain_AndKeepsIt()
    {
        _h.File("ortho/black_masked.tif", "old", indexedSize: 2);
        _h.OwnerHas(3); // "masked" is 6 bytes: it grows by 4
        _h.TrackWrites();

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => Execute(Overwrite()));

        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 3 B left.");
        _h.Ddb.Verify(d => d.AddRaw("ortho/black_masked.tif"), Times.Once);
        File.ReadAllText(_h.Local("ortho/black_masked.tif")).ShouldBe("masked");
        _h.Recorder.SavedPaths.ShouldBeEmpty();
        _h.Calls.ShouldBe(["mask ortho/black_masked.tif near=15 white=False (replace)", "index ortho/black_masked.tif"]);
        _builds.ShouldBeEmpty();
    }

    // FR-XC-27: libddb's mask cannot be cancelled; a cancel that comes while it replaces the copy does not leave the index
    // with the old hash and size: the replaced copy is indexed and built, then the task stops as cancelled
    [Test]
    public async Task Overwrite_CancelledWhileLibddbReplacesTheCopy_IndexesAndBuildsIt_ThenStops()
    {
        _h.File("ortho/black_masked.tif");
        _h.OwnerHasHonouringCancellation();
        using var cts = new CancellationTokenSource();
        CancelDuringTheMask(cts);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            _tool.ExecuteAsync(Overwrite(), _h.Ctx.Object, new Progress<HeavyToolProgress>(), cts.Token));

        _h.Calls.ShouldBe([
            "mask ortho/black_masked.tif near=15 white=False (replace)", "published ortho/black_masked.tif",
            "index ortho/black_masked.tif", "build ortho/black_masked.tif"]);
        File.ReadAllText(_h.Local("ortho/black_masked.tif")).ShouldBe("masked");
    }

    // A new copy cancelled the same way is recorded: the rollback deletes it, it is not indexed
    [Test]
    public async Task NewCopy_CancelledWhileLibddbWritesIt_IsNotIndexed_AndStaysRecordedForTheRollback()
    {
        _h.OwnerHasHonouringCancellation();
        using var cts = new CancellationTokenSource();
        CancelDuringTheMask(cts);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            _tool.ExecuteAsync(Request(), _h.Ctx.Object, new Progress<HeavyToolProgress>(), cts.Token));

        _h.Calls.ShouldBe(["mask ortho/black_masked.tif near=15 white=False"]);
        _h.Recorder.SavedPaths.ShouldBe(["ortho/black_masked.tif", "ortho/black_masked.tif.msk"]);
    }

    private void CancelDuringTheMask(CancellationTokenSource cts, string? sidecar = null) =>
        _h.Ddb.Setup(d => d.MaskBorders("ortho/black.tif", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Callback<string, string, int, bool, bool>((_, output, near, white, overwrite) =>
            {
                _h.Calls.Add($"mask {output} near={near} white={white}" + (overwrite ? " (replace)" : ""));
                File.WriteAllText(_h.Local(output), "masked");
                if (sidecar != null)
                    File.WriteAllText(_h.Local(output) + ".msk", sidecar);
                cts.Cancel();
            });

    // A mask that writes the GDAL sidecar next to the copy
    private void MaskWritesASidecar(string content) =>
        _h.Ddb.Setup(d => d.MaskBorders("ortho/black.tif", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<bool>()))
            .Callback<string, string, int, bool, bool>((_, output, near, white, overwrite) =>
            {
                _h.Calls.Add($"mask {output} near={near} white={white}" + (overwrite ? " (replace)" : ""));
                File.WriteAllText(_h.Local(output), "masked");
                File.WriteAllText(_h.Local(output) + ".msk", content);
            });

    // FR-XC-27: the sidecar of a new copy is in the saved record before the mask runs, so that a rollback removes it with
    // the copy (no orphan .msk), and it is stamped once written
    [Test]
    public async Task NewCopy_ItsSidecarIsRecordedBeforeTheMask_AndStampedOnceWritten()
    {
        MaskWritesASidecar("mm");
        _h.TrackWrites();

        await Execute(Request());

        _h.Calls.IndexOf("record ortho/black_masked.tif.msk").ShouldBeLessThan(
            _h.Calls.IndexOf("mask ortho/black_masked.tif near=15 white=False"));
        _h.Recorder.SavedPaths.ShouldBe(["ortho/black_masked.tif", "ortho/black_masked.tif.msk"]);
        _h.Recorder.Stamped.Keys.ShouldBe(["ortho/black_masked.tif", "ortho/black_masked.tif.msk"], ignoreOrder: true);
    }

    // FR-XC-07: a sidecar that was there before the task is not the task's: never recorded, so no rollback deletes it
    [Test]
    public async Task NewCopy_ASidecarThatWasThereBefore_IsNotRecorded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_h.Local("ortho/black_masked.tif.msk"))!);
        File.WriteAllText(_h.Local("ortho/black_masked.tif.msk"), "theirs");
        _h.TrackWrites();

        await Execute(Request());

        _h.Recorder.SavedPaths.ShouldBe(["ortho/black_masked.tif"]);
    }

    // FR-XC-26: the sidecar counts in the growth: "masked" (6 bytes) fits in 7, with its 2-byte sidecar it does not
    [Test]
    public async Task NewCopy_TheSizeOfItsSidecarCountsInTheQuotaCheck()
    {
        MaskWritesASidecar("mm");
        _h.OwnerHas(7);

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => Execute(Request()));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 7 B left.");
        _builds.ShouldBeEmpty();
    }

    [Test]
    public async Task NewCopy_WithoutASidecar_FitsByItsOwnSize()
    {
        _h.OwnerHas(6);

        await Execute(Request());

        _builds.ShouldHaveSingleItem();
    }

    [Test]
    public async Task ACopyAnotherWriterCreatedWhileTheRecordWasSaved_IsKept_AndRefused()
    {
        _h.Recorder.OnRecord = _ => File.WriteAllText(_h.Local("ortho/black_masked.tif"), "theirs");

        await Should.ThrowAsync<NameClashException>(() => Execute(Request()));

        File.ReadAllText(_h.Local("ortho/black_masked.tif")).ShouldBe("theirs");
        _h.Ddb.Verify(d => d.MaskBorders(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
        _h.Recorder.Forgotten.ShouldBe(["ortho/black_masked.tif", "ortho/black_masked.tif.msk"]);
    }
}
