using Registry.Web.Test.Support;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Registry.Web.Services.Ports;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// BuildTool forwards to HangfireUtils: an empty path runs the pending-build path (here the
/// native fallback, as there is no Hangfire job context), a path builds that entry. A build that finds files missing
/// returns as libddb defers it: its log says which files it waits for, not that the build is complete.
/// </summary>
[TestFixture]
public class BuildToolTests
{
    private sealed class Progress : System.IProgress<HeavyToolProgress>
    {
        public List<HeavyToolProgress> Items { get; } = [];
        public void Report(HeavyToolProgress value) => Items.Add(value);
    }

    private static (Mock<IDDB> Ddb, IHeavyToolExecutionContext Ctx) Context()
    {
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(x => x.DatasetFolderPath).Returns("/data/datasets/o/d");
        var ctx = new Mock<IHeavyToolExecutionContext>();
        ctx.SetupGet(x => x.Ddb).Returns(ddb.Object);
        ctx.SetupGet(x => x.Builds).Returns(TestJobs.DdbJobs());
        return (ddb, ctx.Object);
    }

    // libddb's view of the entry after the build: complete unless a test says it waits
    private readonly Mock<IBuildOutcomeClassifier> _outcomes = new();

    [SetUp]
    public void SetUp() =>
        _outcomes.Setup(o => o.Classify(It.IsAny<IDDB>(), It.IsAny<string>(), null, true))
            .Returns(new ClassifiedBuild(BuildOutcome.Completed, "hash"));

    private BuildTool Tool() => new(_outcomes.Object);

    private static HeavyToolRequest Request(string? path) =>
        new("build", "1", "o", "d", path, JsonDocument.Parse("{}").RootElement);

    [Test]
    public async Task ExecuteAsync_EmptyPath_RunsPendingBuildAndReportsNeutralCompletion()
    {
        var (ddb, ctx) = Context();
        var progress = new Progress();

        var result = await Tool().ExecuteAsync(Request(null), ctx, progress, CancellationToken.None);

        result.ShouldBeNull();
        ddb.Verify(x => x.BuildPending(null, false), Times.Once);
        ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        ddb.Verify(x => x.BuildWithProgress(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<BuildOptions?>(),
            It.IsAny<System.Action<double, string?>>()), Times.Never);
        progress.Items.ShouldContain(p => p.LogChunk == "Pending build check complete");
        progress.Items.ShouldNotContain(p => p.LogChunk == "Build complete");
    }

    [Test]
    public async Task ExecuteAsync_WithPath_BuildsThatEntry()
    {
        var (ddb, ctx) = Context();
        var progress = new Progress();

        await Tool().ExecuteAsync(Request("sub/m.obj"), ctx, progress, CancellationToken.None);

        ddb.Verify(x => x.BuildWithProgress("sub/m.obj", false, null, It.IsAny<System.Action<double, string?>>()),
            Times.Once);
        ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        progress.Items.ShouldContain(p => p.LogChunk == "Build complete");
    }

    [Test]
    public async Task ExecuteAsync_ABuildThatFindsFilesMissing_SaysWhatItWaitsFor_NotThatItIsComplete()
    {
        var (ddb, ctx) = Context();
        _outcomes.Setup(o => o.Classify(ddb.Object, "house.obj", null, true))
            .Returns(new ClassifiedBuild(BuildOutcome.WaitingForFiles, "hash", ["odm_textured_model_geo.mtl", "t.png"]));
        var progress = new Progress();

        await Tool().ExecuteAsync(Request("house.obj"), ctx, progress, CancellationToken.None);

        ddb.Verify(x => x.BuildWithProgress("house.obj", false, null, It.IsAny<System.Action<double, string?>>()),
            Times.Once);
        progress.Items.ShouldContain(p => p.LogChunk == "Waiting for odm_textured_model_geo.mtl, t.png");
        progress.Items.ShouldNotContain(p => p.LogChunk == "Build complete");
    }

    // FR-PRC-42: the build reports the percent and the phase libddb reports (DDBBuildEx2), in order, with a log line at
    // each new phase; a build before libddb has measured anything has no percent
    [Test]
    public async Task ExecuteAsync_ForwardsThePercentAndThePhaseOfTheBuild()
    {
        var (ddb, ctx) = Context();
        ddb.Setup(x => x.BuildWithProgress("ortho.tif", false, null, It.IsAny<System.Action<double, string?>>()))
            .Callback<string?, bool, BuildOptions?, System.Action<double, string?>>((_, _, _, report) =>
            {
                report(-1, "georaster");
                report(0.1, "warping");
                report(0.25, "warping");
                report(0.5, "overviews");
                report(0.9, "compressing");
                report(0.9, "publishing");
                report(1, null);
            });
        var progress = new Progress();

        await Tool().ExecuteAsync(Request("ortho.tif"), ctx, progress, CancellationToken.None);

        var reported = progress.Items.Select(p => (p.Fraction, p.Phase, p.LogChunk)).ToList();
        reported.ShouldBe(new List<(double, string?, string?)>
        {
            (-1, "building", "Building 'ortho.tif'"),
            (-1, "georaster", "georaster"),
            (0.1, "warping", "warping (10%)"),
            (0.25, "warping", null),
            (0.5, "overviews", "overviews (50%)"),
            (0.9, "compressing", "compressing (90%)"),
            (0.9, "publishing", "publishing (90%)"),
            (1, "done", "done (100%)"),
            (1, "done", "Build complete")
        });
    }
}
