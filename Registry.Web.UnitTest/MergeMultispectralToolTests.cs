using System;
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

    private static HeavyToolRequest Request(string output, bool? overwrite = null) =>
        new("merge-multispectral", "1", "org", "ds", null, JsonSerializer.SerializeToElement(new
        {
            paths = new[] { "bands/b1.tif", "bands/b2.tif" }, outputPath = output, overwrite
        }));

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
}
