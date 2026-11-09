using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// A dataset folder on disk with a mocked index and a mocked execution context, for the tools that write one output
/// file (align, merge): the files it lists exist on disk and in the index, <see cref="Calls"/> records the native
/// write, the marker and the index in their order.
/// </summary>
internal sealed class OutputToolHarness : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "output-tool-" + Guid.NewGuid().ToString("N"));
    public Mock<IDDB> Ddb { get; } = new();
    public Mock<IHeavyToolExecutionContext> Ctx { get; } = new();
    public List<string> Calls { get; } = [];

    /// <summary>The write record of the task (FR-XC-27): what the tool records, in <see cref="Calls"/> too.</summary>
    public FakeTaskWriteRecorder Recorder { get; } = new();

    private readonly List<EntrySummary> _entries = [];

    public OutputToolHarness()
    {
        Directory.CreateDirectory(Folder);
        Ddb.SetupGet(d => d.DatasetFolderPath).Returns(Folder);
        Ddb.Setup(d => d.GetLocalPath(It.IsAny<string>())).Returns<string>(Local);
        Ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns<string>(p => _entries.Any(e => e.Path == p));
        Ddb.Setup(d => d.GetEntry(It.IsAny<string>())).Returns<string>(p =>
            _entries.FirstOrDefault(e => e.Path == p) is { } e ? new Entry { Path = e.Path, Type = e.Type, Size = 1 } : null);
        Ddb.Setup(d => d.FindSummaries(It.IsAny<EntryQuery>())).Returns<EntryQuery>(q =>
            _entries.Where(e => Parent(e.Path) == (q.Path ?? string.Empty)).ToList());
        Ddb.Setup(d => d.AddRaw(It.IsAny<string>())).Callback<string>(p => Calls.Add("index " + p));

        Ctx.SetupGet(c => c.Ddb).Returns(Ddb.Object);
        Ctx.SetupGet(c => c.TaskId).Returns("task-1");
        Ctx.SetupGet(c => c.Dataset).Returns(new DatasetKey("org", Guid.NewGuid()));
        Ctx.SetupGet(c => c.WriteRecord).Returns(Recorder);
        Ctx.Setup(c => c.MarkOutputPublished(It.IsAny<string>())).Callback<string>(p => Calls.Add("published " + p));
    }

    /// <summary>From now on <see cref="Calls"/> also holds "record path" and "commit", in the order they happen.</summary>
    public void TrackWrites()
    {
        Recorder.OnRecord = p => Calls.Add("record " + p);
        Recorder.OnCommit = () => Calls.Add("commit");
    }

    public string Local(string path) => Path.Combine(Folder, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A file in the index and on disk (its folders too), with <paramref name="content"/>.</summary>
    public void File(string path, string content = "original")
    {
        var segments = path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var folder = string.Join('/', segments.Take(i));
            if (_entries.All(e => e.Path != folder))
                _entries.Add(new EntrySummary { Path = folder, Type = EntryType.Directory });
        }
        _entries.Add(new EntrySummary { Path = path, Type = EntryType.GeoRaster });
        Directory.CreateDirectory(Path.GetDirectoryName(Local(path))!);
        System.IO.File.WriteAllText(Local(path), content);
    }

    public void PublishedBefore(string path) => Ctx.SetupGet(c => c.PublishedOutput).Returns(path);

    /// <summary>The task was queued before the overwrite rule (its record declares no output paths).</summary>
    public void QueuedBeforeOverwriteRule() => Ctx.SetupGet(c => c.QueuedBeforeOverwriteRule).Returns(true);

    /// <summary>A folder in the index (not on disk).</summary>
    public void IndexedFolder(string path) => _entries.Add(new EntrySummary { Path = path, Type = EntryType.Directory });

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    public void Dispose()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, true);
    }
}

/// <summary>
/// The output rules of <see cref="AlignRasterTool"/> (FR-PRC-01, FR-XC-07, D-040): it declares its inputs and its
/// output, refuses an existing output without <c>overwrite</c> (409 with the clash message and the existing path)
/// and leaves it intact, replaces it with <c>overwrite</c> (at the path of an entry that differs only in case), marks
/// the output written right after the native call, and a later attempt only indexes what an earlier one wrote.
/// </summary>
[TestFixture]
public class AlignRasterToolTests
{
    private OutputToolHarness _h = null!;
    private readonly AlignRasterTool _tool = new(TestPaths.Policy);

    [SetUp]
    public void SetUp()
    {
        _h = new OutputToolHarness();
        _h.File("src.tif");
        _h.File("ref.tif");
        _h.Ddb.Setup(d => d.ValidateAlignRaster("src.tif", "ref.tif")).Returns("{\"ok\":true}");
        _h.Ddb.Setup(d => d.AlignRaster("src.tif", "ref.tif", It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string, string>((_, _, output, _) =>
            {
                _h.Calls.Add("align " + output);
                File.WriteAllText(_h.Local(output), "aligned");
            })
            .Returns("{ \"ok\": true, \"shift\": 1 }");
    }

    [TearDown]
    public void TearDown() => _h.Dispose();

    private static HeavyToolRequest Request(string output, bool? overwrite = null) =>
        new("align-raster", "1", "org", "ds", null, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["sourcePath"] = "src.tif", ["referencePath"] = "ref.tif", ["outputPath"] = output,
            ["overwrite"] = overwrite
        }.Where(kv => kv.Value != null).ToDictionary()));

    private Task Execute(HeavyToolRequest request) =>
        _tool.ExecuteAsync(request, _h.Ctx.Object, new Progress<HeavyToolProgress>(), CancellationToken.None);

    // FR-PRC-06: the validation of libddb refuses rasters that do not overlap (after reprojection when their CRS
    // differ): the submit is a 400 (ArgumentException) that says so, and nothing is written
    [Test]
    public async Task NativeValidationRefusal_IsAnArgumentErrorWithItsMessage()
    {
        _h.Ddb.Setup(d => d.ValidateAlignRaster("src.tif", "ref.tif")).Returns(
            "{\"ok\":false,\"errors\":[\"Insufficient overlap after reprojection (0%)\"],\"warnings\":[]," +
            "\"summary\":{\"crsMismatch\":true,\"overlapPercent\":0}}");

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            _tool.ValidateAsync(Request("out/aligned.tif"), _h.Ctx.Object, CancellationToken.None));

        ex.Message.ShouldContain("Insufficient overlap after reprojection (0%)");
        ex.ParamName.ShouldBeNull("the message is shown as it is, without a parameter name");
        _h.Ddb.Verify(d => d.AlignRaster(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public void DeclaresItsInputsOutputAndLabel()
    {
        var request = Request("./out/aligned.tif");

        _tool.InputPaths(request).ShouldBe(["src.tif", "ref.tif"]);
        _tool.OutputPaths(request).ShouldBe(["out/aligned.tif"]);
        _tool.Label(request).ShouldBe("→ out/aligned.tif");
        _tool.InputSchema.RootElement.GetProperty("properties").GetProperty("overwrite")
            .GetProperty("type").GetString().ShouldBe("boolean");
    }

    [Test]
    public async Task ExistingOutput_WithoutOverwrite_Is409WithItsPath()
    {
        _h.File("out/Aligned.tif");

        var ex = await Should.ThrowAsync<NameClashException>(() =>
            _tool.ValidateAsync(Request("out/aligned.tif"), _h.Ctx.Object, CancellationToken.None));

        ex.StatusCode.ShouldBe(409);
        ex.Message.ShouldBe("A file or folder named 'Aligned.tif' already exists in this folder");
        ex.ConflictPath.ShouldBe("out/Aligned.tif");
    }

    [Test]
    public async Task ExistingOutput_WithoutOverwrite_IsLeftIntact_WhenTheJobRuns()
    {
        _h.File("out/a.tif");

        await Should.ThrowAsync<NameClashException>(() => Execute(Request("out/a.tif")));

        File.ReadAllText(_h.Local("out/a.tif")).ShouldBe("original");
        _h.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Overwrite_ReplacesTheOutput_MarksIt_ThenIndexesIt()
    {
        _h.File("out/a.tif");
        var request = Request("out/a.tif", overwrite: true);

        await _tool.ValidateAsync(request, _h.Ctx.Object, CancellationToken.None);
        await Execute(request);

        _h.Calls.ShouldBe(["align out/a.tif", "published out/a.tif", "index out/a.tif"]);
        File.ReadAllText(_h.Local("out/a.tif")).ShouldBe("aligned");
    }

    [Test]
    public async Task Overwrite_OfANameInAnotherLetterCase_WritesTheExistingEntry()
    {
        _h.File("Out/A.TIF");

        await Execute(Request("out/a.tif", overwrite: true));

        _h.Calls.ShouldBe(["align Out/A.TIF", "published Out/A.TIF", "index Out/A.TIF"]);
    }

    [Test]
    public async Task NewOutput_IsWritten_WithoutOverwrite()
    {
        await Execute(Request("new.tif"));

        _h.Calls.ShouldBe(["align new.tif", "published new.tif", "index new.tif"]);
    }

    [Test]
    public async Task MarkerWhoseFileIsGone_WritesTheOutputAgain()
    {
        _h.PublishedBefore("new.tif");

        await Execute(Request("new.tif"));

        _h.Calls.ShouldBe(["align new.tif", "published new.tif", "index new.tif"]);
    }

    [Test]
    public async Task UnreadableReport_AfterTheWrite_StillLeavesTheMarker()
    {
        _h.Ddb.Setup(d => d.AlignRaster("src.tif", "ref.tif", "new.tif", It.IsAny<string>())).Returns("not json");

        await Should.ThrowAsync<JsonException>(() => Execute(Request("new.tif")));

        _h.Ctx.Verify(c => c.MarkOutputPublished("new.tif"), Times.Once);
    }

    [Test]
    public async Task NewOutput_InAFolderOfAnotherLetterCase_IsWrittenInTheExistingFolder()
    {
        _h.IndexedFolder("out");
        Directory.CreateDirectory(_h.Local("out"));

        await Execute(Request("Out/new.tif"));

        _h.Calls.ShouldBe(["align out/new.tif", "published out/new.tif", "index out/new.tif"]);
    }

    [Test]
    public async Task TaskQueuedBeforeTheOverwriteRule_WithoutTheField_ReplacesAtRunTime()
    {
        _h.File("out/a.tif");
        _h.QueuedBeforeOverwriteRule();
        var request = Request("out/a.tif");

        await _tool.ValidateAsync(request, _h.Ctx.Object, CancellationToken.None);
        await Execute(request);

        _h.Calls.ShouldBe(["align out/a.tif", "published out/a.tif", "index out/a.tif"]);
    }

    [Test]
    public async Task TaskQueuedBeforeTheOverwriteRule_WithOverwriteFalse_IsRefused()
    {
        _h.File("out/a.tif");
        _h.QueuedBeforeOverwriteRule();

        await Should.ThrowAsync<NameClashException>(() => Execute(Request("out/a.tif", overwrite: false)));
    }

    [Test]
    public async Task IndexFailure_AfterTheWrite_KeepsTheMarker()
    {
        _h.Ddb.Setup(d => d.AddRaw("new.tif")).Throws(new DdbBusyException("busy"));

        await Should.ThrowAsync<DdbBusyException>(() => Execute(Request("new.tif")));

        _h.Ctx.Verify(c => c.MarkOutputPublished("new.tif"), Times.Once);
    }

    [Test]
    public async Task LaterAttempt_AfterTheWrite_SkipsTheCheckAndTheNativeCall()
    {
        // The first attempt wrote the output, then failed to index it: no overwrite, and the file exists
        _h.File("new.tif", "aligned");
        _h.PublishedBefore("new.tif");
        var request = Request("new.tif");

        await _tool.ValidateAsync(request, _h.Ctx.Object, CancellationToken.None);
        await Execute(request);

        _h.Calls.ShouldBe(["index new.tif"]);
        _h.Ddb.Verify(d => d.AlignRaster(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        File.ReadAllText(_h.Local("new.tif")).ShouldBe("aligned");
    }

    // FR-XC-26, FR-XC-27 (D-045): the output the alignment creates is in the write record before it is written

    [Test]
    public void AddsDatasetFiles() => _tool.AddsDatasetFiles.ShouldBeTrue();

    [Test]
    public async Task NewOutput_IsRecordedBeforeTheNativeAlignment_FlushedAndStampedAfterIt()
    {
        _h.TrackWrites();

        await Execute(Request("out/new.tif"));

        _h.Calls.ShouldBe(["record out/new.tif", "align out/new.tif", "published out/new.tif", "index out/new.tif"]);
        _h.Recorder.Folders.ShouldBe(["out"]);
        _h.Recorder.Paths.ShouldBe(["out/new.tif"]);
        _h.Recorder.Flushes.ShouldBeGreaterThan(0, "the record is written before the long alignment starts");
        _h.Recorder.Stamped.ShouldContainKey("out/new.tif");
    }

    [Test]
    public async Task NewOutput_AndItsNewFolders_AreSavedInTheRecord_BeforeEitherExists()
    {
        var saved = false;
        _h.Ddb.Setup(d => d.AlignRaster("src.tif", "ref.tif", It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string, string>((_, _, output, _) =>
            {
                // §8.1: a process lost from here on leaves a record that rolls the output back
                _h.Recorder.SavedPaths.ShouldBe([output]);
                _h.Recorder.SavedFolders.ShouldBe(["out"]);
                Directory.Exists(_h.Local("out")).ShouldBeTrue("created once it is in the saved record");
                saved = true;
                File.WriteAllText(_h.Local(output), "aligned");
            })
            .Returns("{ \"ok\": true }");

        await Execute(Request("out/new.tif"));

        saved.ShouldBeTrue();
    }

    [Test]
    public async Task AnOutputAnotherWriterCreatesBeforeLibddbWrites_IsKept_AndForgotten()
    {
        // Created while the record is saved: after the first check, before the native call
        _h.Recorder.OnRecord = p => File.WriteAllText(_h.Local(p), "somebody else's");

        await Should.ThrowAsync<NameClashException>(() => Execute(Request("new.tif")));

        File.ReadAllText(_h.Local("new.tif")).ShouldBe("somebody else's");
        _h.Recorder.Paths.ShouldBeEmpty("a rollback must not delete the other writer's file");
        _h.Recorder.Forgotten.ShouldBe(["new.tif"]);
        _h.Ddb.Verify(d => d.AlignRaster(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task APartialOutputLibddbLeavesWhenItFails_StaysRecorded_SoTheRollbackDeletesIt()
    {
        _h.Ddb.Setup(d => d.AlignRaster("src.tif", "ref.tif", It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string, string>((_, _, output, _) => File.WriteAllText(_h.Local(output), "partial"))
            .Throws(new InvalidOperationException("Cannot align raster: the write failed"));

        await Should.ThrowAsync<InvalidOperationException>(() => Execute(Request("new.tif")));

        File.Exists(_h.Local("new.tif")).ShouldBeTrue();
        _h.Recorder.SavedPaths.ShouldBe(["new.tif"], "the task's own partial file: the rollback deletes it");
        _h.Recorder.Forgotten.ShouldBeEmpty();
        _h.Recorder.Stamped.ShouldBeEmpty();
    }

    [Test]
    public async Task Overwrite_OfAnExistingOutput_IsNotRecorded_SoARollbackNeverDeletesTheFileThatWasThere()
    {
        _h.File("out/a.tif");

        await Execute(Request("out/a.tif", overwrite: true));

        File.ReadAllText(_h.Local("out/a.tif")).ShouldBe("aligned");
        _h.Recorder.Paths.ShouldBeEmpty();
        _h.Recorder.Folders.ShouldBeEmpty();
        _h.Recorder.Stamped.ShouldBeEmpty();
    }

    [Test]
    public async Task Overwrite_CancelledBeforeTheAlignment_KeepsTheExistingOutput_AndRecordsNothing()
    {
        _h.File("out/a.tif");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _tool.ExecuteAsync(
            Request("out/a.tif", overwrite: true), _h.Ctx.Object, new Progress<HeavyToolProgress>(), cts.Token));

        File.ReadAllText(_h.Local("out/a.tif")).ShouldBe("original");
        _h.Recorder.Paths.ShouldBeEmpty();
    }

    [Test]
    public async Task ANameThatAppearedSinceTheCheck_WithoutOverwrite_IsNeverReplaced_NorRecorded()
    {
        // The file is created when the tool asks where the output goes: after the check of the worker, before the write
        _h.Ddb.Setup(d => d.GetLocalPath("late.tif")).Returns(() =>
        {
            File.WriteAllText(_h.Local("late.tif"), "somebody else's");
            return _h.Local("late.tif");
        });

        await Should.ThrowAsync<NameClashException>(() => Execute(Request("late.tif")));

        File.ReadAllText(_h.Local("late.tif")).ShouldBe("somebody else's");
        _h.Recorder.Paths.ShouldBeEmpty();
    }
}
