using Registry.Web.Test.Support;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Tools;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// BuildTool forwards to HangfireUtils: an empty path runs the pending-build path (here the
/// native fallback, as there is no Hangfire job context), a path builds that entry.
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

    private static HeavyToolRequest Request(string? path) =>
        new("build", "1", "o", "d", path, JsonDocument.Parse("{}").RootElement);

    [Test]
    public async Task ExecuteAsync_EmptyPath_RunsPendingBuildAndReportsNeutralCompletion()
    {
        var (ddb, ctx) = Context();
        var progress = new Progress();

        var result = await new BuildTool().ExecuteAsync(Request(null), ctx, progress, CancellationToken.None);

        result.ShouldBeNull();
        ddb.Verify(x => x.BuildPending(null, false), Times.Once);
        ddb.Verify(x => x.Build(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        progress.Items.ShouldContain(p => p.LogChunk == "Pending build check complete");
        progress.Items.ShouldNotContain(p => p.LogChunk == "Build complete");
    }

    [Test]
    public async Task ExecuteAsync_WithPath_BuildsThatEntry()
    {
        var (ddb, ctx) = Context();
        var progress = new Progress();

        await new BuildTool().ExecuteAsync(Request("sub/m.obj"), ctx, progress, CancellationToken.None);

        ddb.Verify(x => x.Build("sub/m.obj", null, false), Times.Once);
        ddb.Verify(x => x.BuildPending(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        progress.Items.ShouldContain(p => p.LogChunk == "Build complete");
    }
}
