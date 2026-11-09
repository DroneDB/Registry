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
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>()))
            .Callback<string[], string>((_, output) =>
            {
                _h.Calls.Add("merge " + output);
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

        _h.Calls.ShouldBe(["merge bands/merged.tif", "published bands/merged.tif", "index bands/merged.tif"]);
        File.ReadAllText(_h.Local("bands/merged.tif")).ShouldBe("merged");
    }

    [Test]
    public async Task Overwrite_OfANameInAnotherLetterCase_WritesTheExistingEntry()
    {
        _h.File("bands/Merged.TIF");

        await Execute(Request("BANDS/merged.tif", overwrite: true));

        _h.Calls.ShouldBe(["merge bands/Merged.TIF", "published bands/Merged.TIF", "index bands/Merged.TIF"]);
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
        _h.Ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>()), Times.Never);
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

        _h.Calls.ShouldBe(["merge bands/merged.tif", "published bands/merged.tif", "index bands/merged.tif"]);
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
        _h.Ddb.Setup(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>()))
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
        _h.Ddb.Verify(d => d.MergeMultispectral(It.IsAny<string[]>(), It.IsAny<string>()), Times.Never);
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
}
