using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The output rules of <see cref="MergeMultispectralTool"/> (FR-PRC-01, FR-PRC-02, FR-XC-07, D-040), as for align: the
/// bands are its inputs, an existing output is refused without <c>overwrite</c> and left intact, replaced with it,
/// marked right after the native merge, and a later attempt only indexes what an earlier one wrote.
/// </summary>
[TestFixture]
public class MergeMultispectralToolTests
{
    private OutputToolHarness _h = null!;
    private readonly MergeMultispectralTool _tool = new(TestPaths.Policy);

    [SetUp]
    public void SetUp()
    {
        _h = new OutputToolHarness();
        _h.File("bands/b1.tif");
        _h.File("bands/b2.tif");
        _h.Ddb.Setup(d => d.ValidateMergeMultispectral(It.IsAny<string[]>())).Returns("{\"ok\":true}");
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string[], string, bool>((_, output, overwrite) =>
            {
                // The replace of an existing output is libddb's (DDBMergeMultispectralEx overwrite), never a delete before it
                _h.Calls.Add("merge " + output + (overwrite ? " (replace)" : ""));
                File.WriteAllText(_h.Local(output), "merged");
            });
    }

    [TearDown]
    public void TearDown() => _h.Dispose();

    private static HeavyToolRequest Request(string output, bool? overwrite = null, bool? deleteSources = null,
        string[]? paths = null) =>
        new("merge-multispectral", "1", "org", "ds", null, JsonSerializer.SerializeToElement(new
        {
            paths = paths ?? ["bands/b1.tif", "bands/b2.tif"], outputPath = output, overwrite, deleteSources
        }));

    /// <summary>The log of a run, synchronously (<see cref="Progress{T}"/> posts its reports).</summary>
    private sealed class LogRecorder : IProgress<HeavyToolProgress>
    {
        public List<string> Lines { get; } = [];
        public void Report(HeavyToolProgress value)
        {
            if (value.LogChunk is { } line) Lines.Add(line);
        }
    }

    /// <summary>Records the raster open of the output and the removal of an entry in <see cref="OutputToolHarness.Calls"/>.</summary>
    private void TrackSourceDeletes()
    {
        _h.Ddb.Setup(d => d.GetRasterInfo(It.IsAny<string>())).Callback<string>(p => _h.Calls.Add("open " + p))
            .Returns("{}");
        _h.Ddb.Setup(d => d.Remove(It.IsAny<string>())).Callback<string>(p => _h.Calls.Add("remove " + p));
    }

    private Task Execute(HeavyToolRequest request) =>
        _tool.ExecuteAsync(request, _h.Ctx.Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

    // FR-XC-26, D-039: an output that does not fit what the owner of the organization has left fails the task with the 507
    // text; it is not published nor indexed, and its recorded path is the rollback's to delete
    [Test]
    public async Task AnOutputThatDoesNotFitTheQuotaOfTheOwner_Fails507_AndIsNeitherPublishedNorIndexed()
    {
        _h.OwnerHas(5); // "merged" is 6 bytes

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => Execute(Request("bands/merged.tif")));

        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 5 B left.");
        _h.Calls.ShouldBe(["merge bands/merged.tif"]);
        _h.Recorder.SavedPaths.ShouldBe(["bands/merged.tif"]);
    }

    // FR-XC-26, FR-XC-27, AC-005: a replace is checked for its growth only; when the growth does not fit, the task fails
    // with the 507 text and the output that replaced the file stays on disk and in the index, outside the write record
    [Test]
    public async Task AReplaceWhoseGrowthDoesNotFit_Fails507_AndTheFileStaysOnDiskAndInTheIndex()
    {
        _h.File("bands/merged.tif", "old", indexedSize: 2);
        _h.OwnerHas(3); // "merged" is 6 bytes: it grows by 4

        var ex = await Should.ThrowAsync<QuotaExceededException>(() =>
            Execute(Request("bands/merged.tif", overwrite: true)));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 3 B left.");
        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("merged");
        _h.Calls.ShouldBe(["merge bands/merged.tif (replace)", "index bands/merged.tif"]);
        _h.Recorder.Paths.ShouldBeEmpty();
    }

    [Test]
    public async Task AReplaceWhoseGrowthDoesNotFit_StillFails507_WhenTheIndexIsBusy()
    {
        _h.File("bands/merged.tif", "old", indexedSize: 2);
        _h.OwnerHas(3);
        _h.Ddb.Setup(d => d.AddRaw("bands/merged.tif")).Throws(new DdbBusyException("busy"));

        var ex = await Should.ThrowAsync<QuotaExceededException>(() =>
            Execute(Request("bands/merged.tif", overwrite: true)));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 3 B left.");
        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("merged");
        _h.Recorder.Paths.ShouldBeEmpty();
    }

    [Test]
    public async Task AReplaceWhoseGrowthFits_IsPublishedAndIndexed_ThoughTheWholeOutputWouldNotFit()
    {
        _h.File("bands/merged.tif", "old", indexedSize: 4);
        _h.OwnerHas(2); // "merged" is 6 bytes: it grows by 2

        await Execute(Request("bands/merged.tif", overwrite: true));

        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("merged");
        _h.Calls.ShouldBe(["merge bands/merged.tif (replace)", "published bands/merged.tif", "index bands/merged.tif"]);
    }

    // The plan of a replace tells the runner the size the owner's use counts for the file it replaces
    [Test]
    public void Plan_OfAReplace_CarriesTheIndexedSizeOfTheReplacedFile()
    {
        _h.File("bands/merged.tif", "old", indexedSize: 40);

        var replace = _tool.Plan(Request("bands/merged.tif", overwrite: true), _h.Ctx.Object);
        var create = _tool.Plan(Request("bands/new.tif"), _h.Ctx.Object);

        replace.ReplacedBytes.ShouldBe(40);
        create.ReplacedBytes.ShouldBe(0);
    }

    [Test]
    public async Task AnOutputThatFitsTheQuotaOfTheOwner_IsPublishedAndIndexed()
    {
        _h.OwnerHas(6);

        await Execute(Request("bands/merged.tif"));

        _h.Calls.ShouldBe(["merge bands/merged.tif", "published bands/merged.tif", "index bands/merged.tif"]);
    }

    [Test]
    public void DeclaresItsBandsOutputAndLabel()
    {
        var request = Request("bands/merged.tif");

        _tool.InputPaths(request).ShouldBe(["bands/b1.tif", "bands/b2.tif"]);
        _tool.OutputPaths(request).ShouldBe(["bands/merged.tif"]);
        _tool.Label(request).ShouldBe("→ bands/merged.tif");
        _tool.InputSchema.RootElement.GetProperty("properties").GetProperty("overwrite")
            .GetProperty("type").GetString().ShouldBe("boolean");
    }

    [Test]
    public async Task ExistingOutput_WithoutOverwrite_Is409WithItsPath()
    {
        _h.File("bands/merged.tif");

        var ex = await Should.ThrowAsync<NameClashException>(() =>
            _tool.ValidateAsync(Request("bands/merged.tif"), _h.Ctx.Object, CancellationToken.None));

        ex.Message.ShouldBe("A file or folder named 'merged.tif' already exists in this folder");
        ex.ConflictPath.ShouldBe("bands/merged.tif");
    }

    [Test]
    public async Task ExistingOutput_WithoutOverwrite_IsLeftIntact_WhenTheJobRuns()
    {
        _h.File("bands/merged.tif");

        await Should.ThrowAsync<NameClashException>(() => Execute(Request("bands/merged.tif")));

        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("original");
        _h.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Overwrite_ReplacesTheOutput_MarksIt_ThenIndexesIt()
    {
        _h.File("bands/merged.tif");
        var request = Request("bands/merged.tif", overwrite: true);

        await _tool.ValidateAsync(request, _h.Ctx.Object, CancellationToken.None);
        await Execute(request);

        _h.Calls.ShouldBe(["merge bands/merged.tif (replace)", "published bands/merged.tif", "index bands/merged.tif"]);
        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("merged");
    }

    [Test]
    public async Task Overwrite_OfANameInAnotherLetterCase_WritesTheExistingEntry()
    {
        _h.File("bands/Merged.TIF");

        await Execute(Request("BANDS/merged.tif", overwrite: true));

        _h.Calls.ShouldBe(["merge bands/Merged.TIF (replace)", "published bands/Merged.TIF", "index bands/Merged.TIF"]);
    }

    [Test]
    public async Task LaterAttempt_AfterTheWrite_SkipsTheCheckAndTheNativeCall()
    {
        _h.File("bands/merged.tif", "merged");
        _h.PublishedBefore("bands/merged.tif");
        var request = Request("bands/merged.tif");

        await _tool.ValidateAsync(request, _h.Ctx.Object, CancellationToken.None);
        await Execute(request);

        _h.Calls.ShouldBe(["index bands/merged.tif"]);
        _h.Ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task MarkerWhoseFileIsGone_WritesTheOutputAgain()
    {
        _h.PublishedBefore("bands/new.tif");

        await Execute(Request("bands/new.tif"));

        _h.Calls.ShouldBe(["merge bands/new.tif", "published bands/new.tif", "index bands/new.tif"]);
    }

    [Test]
    public async Task TaskQueuedBeforeTheOverwriteRule_WithoutTheField_ReplacesAtRunTime()
    {
        _h.File("bands/merged.tif");
        _h.QueuedBeforeOverwriteRule();
        var request = Request("bands/merged.tif");

        await _tool.ValidateAsync(request, _h.Ctx.Object, CancellationToken.None);
        await Execute(request);

        _h.Calls.ShouldBe(["merge bands/merged.tif (replace)", "published bands/merged.tif", "index bands/merged.tif"]);
    }

    [Test]
    public async Task WithoutTheField_AtSubmit_AnExistingOutputIsRefused()
    {
        _h.File("bands/merged.tif");

        await Should.ThrowAsync<NameClashException>(() =>
            _tool.ValidateAsync(Request("bands/merged.tif"), _h.Ctx.Object, CancellationToken.None));
    }

    [Test]
    public async Task IndexFailure_AfterTheWrite_KeepsTheMarker()
    {
        _h.Ddb.Setup(d => d.AddRaw("bands/new.tif")).Throws(new DdbBusyException("busy"));

        await Should.ThrowAsync<DdbBusyException>(() => Execute(Request("bands/new.tif")));

        _h.Ctx.Verify(c => c.MarkOutputPublished("bands/new.tif"), Times.Once);
    }

    [Test]
    public void DeclaresTheDeleteSourcesFlag()
    {
        var property = _tool.InputSchema.RootElement.GetProperty("properties").GetProperty("deleteSources");

        property.GetProperty("type").GetString().ShouldBe("boolean");
        property.GetProperty("default").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task DeleteSources_DeletesTheBands_OnlyAfterThePublishTheIndexAndTheRasterOpen()
    {
        TrackSourceDeletes();

        await Execute(Request("bands/new.tif", deleteSources: true));

        _h.Calls.ShouldBe([
            "merge bands/new.tif", "published bands/new.tif", "index bands/new.tif", "open bands/new.tif",
            "remove bands/b1.tif", "remove bands/b2.tif"
        ]);
        File.Exists(_h.Local("bands/b1.tif")).ShouldBeFalse();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeFalse();
        File.ReadAllText(_h.Local("bands/new.tif")).ShouldBe("merged");
    }

    [Test]
    public async Task WithoutDeleteSources_TheBandsAreKept()
    {
        TrackSourceDeletes();

        await Execute(Request("bands/new.tif"));

        _h.Ddb.Verify(d => d.Remove(It.IsAny<string>()), Times.Never);
        File.Exists(_h.Local("bands/b1.tif")).ShouldBeTrue();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeTrue();
    }

    [Test]
    public async Task DeleteSources_AFailedMerge_KeepsTheBands()
    {
        TrackSourceDeletes();
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new DdbException("merge failed"));

        await Should.ThrowAsync<DdbException>(() => Execute(Request("bands/new.tif", deleteSources: true)));

        _h.Ddb.Verify(d => d.Remove(It.IsAny<string>()), Times.Never);
        File.Exists(_h.Local("bands/b1.tif")).ShouldBeTrue();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeTrue();
    }

    [Test]
    public async Task DeleteSources_AFailedIndex_KeepsTheBands()
    {
        TrackSourceDeletes();
        _h.Ddb.Setup(d => d.AddRaw("bands/new.tif")).Throws(new DdbBusyException("busy"));

        await Should.ThrowAsync<DdbBusyException>(() => Execute(Request("bands/new.tif", deleteSources: true)));

        _h.Ddb.Verify(d => d.Remove(It.IsAny<string>()), Times.Never);
        File.Exists(_h.Local("bands/b1.tif")).ShouldBeTrue();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeTrue();
    }

    [Test]
    public async Task DeleteSources_AnOutputThatDoesNotOpenAsARaster_KeepsTheBands()
    {
        TrackSourceDeletes();
        _h.Ddb.Setup(d => d.GetRasterInfo("bands/new.tif")).Throws(new DdbException("not a raster"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Execute(Request("bands/new.tif", deleteSources: true)));

        ex.Message.ShouldContain("the source bands were kept");
        _h.Ddb.Verify(d => d.Remove(It.IsAny<string>()), Times.Never);
        File.Exists(_h.Local("bands/b1.tif")).ShouldBeTrue();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeTrue();
    }

    [Test]
    public async Task DeleteSources_ALaterAttempt_SkipsTheBandsAnEarlierAttemptDeleted()
    {
        // The earlier attempt wrote the output and deleted b1, then failed: the retry indexes the output, skips b1
        // (neither indexed nor on disk) and deletes b2, without failing
        TrackSourceDeletes();
        _h.File("bands/new.tif", "merged");
        _h.PublishedBefore("bands/new.tif");
        File.Delete(_h.Local("bands/b1.tif"));
        _h.Ddb.Setup(d => d.EntryExists("bands/b1.tif")).Returns(false);
        _h.Ddb.Setup(d => d.FindSummaries(It.Is<EntryQuery>(q => q.Path == "bands"))).Returns([
            new EntrySummary { Path = "bands/b2.tif", Type = EntryType.GeoRaster },
            new EntrySummary { Path = "bands/new.tif", Type = EntryType.GeoRaster }
        ]);

        await Execute(Request("bands/new.tif", deleteSources: true));

        _h.Calls.ShouldBe(["index bands/new.tif", "open bands/new.tif", "remove bands/b2.tif"]);
        _h.Ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeFalse();
        File.ReadAllText(_h.Local("bands/new.tif")).ShouldBe("merged");
    }

    [Test]
    public async Task DeleteSources_FindsEachBandWithoutRegardToLetterCase()
    {
        // The request names 'images/b3.tif', the dataset holds 'Images/B3.TIF' (D-040): that entry and its file go
        TrackSourceDeletes();
        _h.File("Images/B3.TIF");

        await Execute(Request("bands/new.tif", deleteSources: true, paths: ["images/b3.tif", "bands/b2.tif"]));

        _h.Calls.ShouldContain("remove Images/B3.TIF");
        _h.Calls.ShouldContain("remove bands/b2.tif");
        File.Exists(_h.Local("Images/B3.TIF")).ShouldBeFalse();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeFalse();
    }

    [Test]
    public async Task DeleteSources_ABandThatCannotBeDeleted_IsLogged_AndTheMergeSucceeds()
    {
        TrackSourceDeletes();
        _h.Ddb.Setup(d => d.Remove("bands/b1.tif")).Throws(new DdbException("the file is locked"));
        var log = new LogRecorder();

        await _tool.ExecuteAsync(Request("bands/new.tif", deleteSources: true), _h.Ctx.Object, log, CancellationToken.None);

        log.Lines.ShouldContain("Could not delete source band 'bands/b1.tif': the file is locked");
        _h.Calls.ShouldContain("remove bands/b2.tif");
        File.Exists(_h.Local("bands/b1.tif")).ShouldBeTrue();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeFalse();
        log.Lines.ShouldContain("Merge complete");
    }

    // FR-XC-26, FR-XC-27 (D-045): the output the merge creates is in the write record before it is written

    [Test]
    public void AddsDatasetFiles() => _tool.AddsDatasetFiles.ShouldBeTrue();

    [Test]
    public async Task NewOutput_IsRecordedBeforeTheNativeMerge_FlushedAndStampedAfterIt()
    {
        _h.TrackWrites();

        await Execute(Request("bands/new.tif"));

        _h.Calls.ShouldBe(["record bands/new.tif", "merge bands/new.tif", "published bands/new.tif",
            "index bands/new.tif"]);
        _h.Recorder.Paths.ShouldBe(["bands/new.tif"]);
        _h.Recorder.Flushes.ShouldBeGreaterThan(0, "the record is written before the long merge starts");
        _h.Recorder.Stamped.ShouldContainKey("bands/new.tif");
    }

    [Test]
    public async Task NewOutput_AndItsNewFolders_AreSavedInTheRecord_BeforeEitherExists()
    {
        var saved = false;
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string[], string, bool>((_, output, _) =>
            {
                // §8.1: a process lost from here on leaves a record that rolls the output back
                _h.Recorder.SavedPaths.ShouldBe([output]);
                _h.Recorder.SavedFolders.ShouldBe(["out", "out/deep"]);
                saved = true;
                File.WriteAllText(_h.Local(output), "merged");
            });

        await Execute(Request("out/deep/new.tif"));

        saved.ShouldBeTrue();
    }

    [Test]
    public async Task AnOutputAnotherWriterCreatesBeforeLibddbWrites_IsKept_AndForgotten()
    {
        // Created while the record is saved: after the first check, before the native call
        _h.Recorder.OnRecord = p => File.WriteAllText(_h.Local(p), "somebody else's");

        await Should.ThrowAsync<NameClashException>(() => Execute(Request("bands/new.tif")));

        File.ReadAllText(_h.Local("bands/new.tif")).ShouldBe("somebody else's");
        _h.Recorder.Paths.ShouldBeEmpty("a rollback must not delete the other writer's file");
        _h.Recorder.Forgotten.ShouldBe(["bands/new.tif"]);
        _h.Ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Test]
    public async Task APartialOutputLibddbLeavesWhenItFails_StaysRecorded_SoTheRollbackDeletesIt()
    {
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string[], string, bool>((_, output, _) => File.WriteAllText(_h.Local(output), "partial"))
            .Throws(new InvalidOperationException("Cannot merge multispectral: the write failed"));

        await Should.ThrowAsync<InvalidOperationException>(() => Execute(Request("bands/new.tif")));

        File.Exists(_h.Local("bands/new.tif")).ShouldBeTrue();
        _h.Recorder.SavedPaths.ShouldBe(["bands/new.tif"], "the task's own partial file: the rollback deletes it");
        _h.Recorder.Forgotten.ShouldBeEmpty();
        _h.Recorder.Stamped.ShouldBeEmpty();
    }

    [Test]
    public async Task NewOutput_InNewFolders_RecordsTheFoldersBeforeTheOutput()
    {
        await Execute(Request("out/deep/new.tif"));

        _h.Recorder.Folders.ShouldBe(["out", "out/deep"]);
        _h.Recorder.Paths.ShouldBe(["out/deep/new.tif"]);
    }

    [Test]
    public async Task Overwrite_OfAnExistingOutput_IsNotRecorded_SoARollbackNeverDeletesTheFileThatWasThere()
    {
        _h.File("bands/merged.tif");

        await Execute(Request("bands/merged.tif", overwrite: true));

        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("merged");
        _h.Recorder.Paths.ShouldBeEmpty();
        _h.Recorder.Folders.ShouldBeEmpty();
        _h.Recorder.Stamped.ShouldBeEmpty();
    }

    [Test]
    public async Task Overwrite_FailedMerge_LeavesTheReplacedFileUnchanged()
    {
        // AC-005: libddb replaces the output in one rename once the merge is complete; nothing deletes it before
        _h.File("bands/merged.tif");
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new DdbException("merge failed"));

        await Should.ThrowAsync<DdbException>(() => Execute(Request("bands/merged.tif", overwrite: true)));

        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("original");
        _h.Ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), "bands/merged.tif", true), Times.Once);
        _h.Recorder.Paths.ShouldBeEmpty();
    }

    [Test]
    public async Task NewOutput_IsWrittenWithoutTheReplace_SoAFileThatAppearsMeanwhileIsRefusedByLibddb()
    {
        await Execute(Request("bands/new.tif", overwrite: true));

        _h.Ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), "bands/new.tif", false), Times.Once);
    }

    [Test]
    public async Task Overwrite_CancelledBeforeTheMerge_KeepsTheExistingOutput_AndRecordsNothing()
    {
        _h.File("bands/merged.tif");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _tool.ExecuteAsync(
            Request("bands/merged.tif", overwrite: true), _h.Ctx.Object, new Progress<HeavyToolProgress>(), cts.Token));

        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("original");
        _h.Recorder.Paths.ShouldBeEmpty();
    }

    [Test]
    public async Task Overwrite_CancelledWhileMerging_DoesNotRecordTheReplacedOutput()
    {
        // The decision is made before the existing output is deleted: a cancel in the middle changes nothing
        _h.File("bands/merged.tif");
        using var cts = new CancellationTokenSource();
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback<string[], string, bool>((_, output, _) =>
            {
                File.WriteAllText(_h.Local(output), "merged");
                cts.Cancel();
            });

        try
        {
            await _tool.ExecuteAsync(Request("bands/merged.tif", overwrite: true), _h.Ctx.Object,
                new Progress<HeavyToolProgress>(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // either way the replaced output is not the task's
        }

        _h.Recorder.Paths.ShouldBeEmpty();
    }

    [Test]
    public async Task ANameThatAppearedSinceTheCheck_WithoutOverwrite_IsNeverReplaced_NorRecorded()
    {
        // The file is created when the tool asks where the output goes: after the check of the worker, before the write
        _h.Ddb.Setup(d => d.GetLocalPath("bands/late.tif")).Returns(() =>
        {
            File.WriteAllText(_h.Local("bands/late.tif"), "somebody else's");
            return _h.Local("bands/late.tif");
        });

        await Should.ThrowAsync<NameClashException>(() => Execute(Request("bands/late.tif")));

        File.ReadAllText(_h.Local("bands/late.tif")).ShouldBe("somebody else's");
        _h.Recorder.Paths.ShouldBeEmpty();
    }

    [Test]
    public async Task DeleteSources_CommitsBeforeTheFirstDelete_AndTellsTheDatasetWhichBandsLeft()
    {
        TrackSourceDeletes();
        _h.TrackWrites();

        await Execute(Request("bands/new.tif", deleteSources: true));

        _h.Calls.ShouldBe([
            "record bands/new.tif", "merge bands/new.tif", "published bands/new.tif", "index bands/new.tif",
            "open bands/new.tif", "commit", "remove bands/b1.tif", "remove bands/b2.tif"
        ]);
        _h.Recorder.Committed.ShouldBeTrue();
        _h.Recorder.EntriesLeft.ShouldBe(["bands/b1.tif", "bands/b2.tif"]);
    }

    [Test]
    public async Task DeleteSources_ACommitRefusedByACancel_DeletesNothing()
    {
        TrackSourceDeletes();
        _h.Recorder.RefuseCommit = true;

        await Should.ThrowAsync<TaskWriteRecordClosedException>(() =>
            Execute(Request("bands/new.tif", deleteSources: true)));

        _h.Ddb.Verify(d => d.Remove(It.IsAny<string>()), Times.Never);
        File.Exists(_h.Local("bands/b1.tif")).ShouldBeTrue();
        File.Exists(_h.Local("bands/b2.tif")).ShouldBeTrue();
        _h.Recorder.Paths.ShouldBe(["bands/new.tif"], "the output is the task's: the rollback deletes it");
    }

    [Test]
    public async Task WithoutDeleteSources_TheTaskNeverCommits()
    {
        await Execute(Request("bands/new.tif"));

        _h.Recorder.Committed.ShouldBeFalse();
    }
}
