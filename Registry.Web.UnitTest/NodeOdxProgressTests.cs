using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Data.Models;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.NodeOdx;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Services.Processing;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// U-407: the stage of a photogrammetry run (FR-PRC-32), the node tasks of a failed or canceled run (FR-PRC-36), the
/// help of the node's options (FR-DEV-10) and the queue wait of a build (FR-PRC-42).
/// </summary>
[TestFixture]
public class NodeOdxProgressTests
{
    private static readonly NodeOdxEndpoint Node = new("n1", "http://node", null, null);

    private sealed class Reports : IProgress<HeavyToolProgress>
    {
        public readonly List<HeavyToolProgress> All = [];
        public void Report(HeavyToolProgress value) => All.Add(value);
    }

    private static NodeOdxTaskInfo Info(NodeOdxTaskStatusCode code, double progress, string? error = null) =>
        new("uuid-1", code, error, progress, 2);

    private static (NodeOdxTaskRunner Runner, Mock<INodeOdxClient> Client) Runner(
        IEnumerable<NodeOdxTaskInfo> infos, IEnumerable<string[]> output, bool keepFailed = false)
    {
        var client = new Mock<INodeOdxClient>();
        client.Setup(c => c.CreateTaskAsync(It.IsAny<NodeOdxEndpoint>(), "run", It.IsAny<IReadOnlyList<string>>(),
            null, It.IsAny<CancellationToken>())).ReturnsAsync("uuid-1");
        client.SetupSequence(c => c.GetTaskInfoAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<CancellationToken>()))
            .Pipe(setup => infos.Aggregate(setup, (s, i) => s.ReturnsAsync(i)));
        var lines = output.GetEnumerator();
        client.Setup(c => c.GetTaskOutputAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<int>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(() => lines.MoveNext() ? lines.Current : []);
        client.Setup(c => c.DownloadAssetAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", "all.zip", It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<NodeOdxEndpoint, string, string, string, CancellationToken>((_, _, _, path, _) =>
                System.IO.File.WriteAllBytesAsync(path, [1]));
        var platform = TestSettings.Options<ProcessingPlatformSettings>(new AppSettings
        {
            ProcessingPlatform = new ProcessingPlatformSettings
                { RemoteNodePollIntervalSeconds = 1, KeepFailedNodeTasks = keepFailed }
        });
        return (new NodeOdxTaskRunner(client.Object, new NodeOdxNodeRegistry(platform), TestScratch.Provider, platform), client);
    }

    // ---- FR-PRC-32 ----

    [Test]
    public async Task ThePhase_FollowsTheStageTheOdxOutputSays()
    {
        var (runner, _) = Runner(
            [Info(NodeOdxTaskStatusCode.Running, 5), Info(NodeOdxTaskStatusCode.Running, 20),
             Info(NodeOdxTaskStatusCode.Running, 60), Info(NodeOdxTaskStatusCode.Running, 85),
             Info(NodeOdxTaskStatusCode.Completed, 100)],
            [["[INFO]    Running dataset stage"], ["[INFO]    Running opensfm stage"],
             ["[INFO]    Running odm_meshing stage"], ["[INFO]    Running odm_dem stage"], []]);
        var reports = new Reports();

        using (await runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, reports, CancellationToken.None))
        {
        }

        // submitting, queued, then one report per line or poll, the download
        reports.All.Select(r => r.Phase).ShouldBe(
            ["submitting", "queued", "dataset", "opensfm", "meshing", "DEM", "completed", "downloading"]);
    }

    [TestCase("[INFO]    Running opensfm stage", "opensfm")]
    [TestCase("[INFO]    Running openmvs stage", "openmvs")]
    [TestCase("[INFO]    Running odm_meshing stage", "meshing")]
    [TestCase("[INFO]    Running mvs_texturing stage", "texturing")]
    [TestCase("[INFO]    Running odm_dem stage", "DEM")]
    [TestCase("[INFO]    Running odm_orthophoto stage", "orthophoto")]
    [TestCase("[INFO]    running \"/code/SuperBuild/install/bin/opensfm/bin/opensfm\" extract_metadata", "opensfm")]
    [TestCase("[INFO]    running /code/SuperBuild/install/bin/OpenMVS/DensifyPointCloud --input x", "openmvs")]
    public void AStageLine_NamesTheStage(string line, string label) => NodeOdxTaskRunner.StageOf(line)!.Value.Label.ShouldBe(label);

    [TestCase("Initializing ODM 3.8.4")]
    [TestCase("[INFO]    Loading 18 images")]
    public void AnotherLine_NamesNoStage(string line) => NodeOdxTaskRunner.StageOf(line).ShouldBeNull();

    [Test]
    public async Task AToolALaterStageStartsAgain_DoesNotTakeTheRunBack()
    {
        var (runner, _) = Runner(
            [Info(NodeOdxTaskStatusCode.Running, 40), Info(NodeOdxTaskStatusCode.Running, 41), Info(NodeOdxTaskStatusCode.Completed, 100)],
            [["[INFO]    Running openmvs stage"], ["running \"/bin/opensfm/bin/opensfm\" export_visualsfm"], []]);
        var reports = new Reports();

        using (await runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, reports, CancellationToken.None))
        {
        }

        reports.All.Skip(2).Take(2).Select(r => r.Phase).ShouldBe(["openmvs", "openmvs"]);
    }

    [Test]
    public async Task WhenTheNodePercentStands_TheStageGivesTheProgress()
    {
        // The node stays at 10 % through the poll that says "Running odm_dem stage": ODX sends 80 % when odm_dem starts
        var (runner, _) = Runner(
            [Info(NodeOdxTaskStatusCode.Running, 10), Info(NodeOdxTaskStatusCode.Running, 10), Info(NodeOdxTaskStatusCode.Completed, 100)],
            [[], ["[INFO]    Running odm_dem stage"], []]);
        var reports = new Reports();

        using (await runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, reports, CancellationToken.None))
        {
        }

        // 80 % of the run, in the 95 % that the node's part of the task is
        reports.All.Single(r => r.Phase == "DEM").Fraction.ShouldBe(0.76, 1e-9);
        reports.All.Select(r => r.Fraction).Where(f => f >= 0).ShouldBeInOrder(SortDirection.Ascending);
    }

    [Test]
    public async Task DuringOpensfm_TheProgressNeverGetsAheadOfTheNodePercent()
    {
        // ODX sends 5 % when opensfm starts (dataset ends at 5 %) and stays there for a while
        var (runner, _) = Runner(
            [Info(NodeOdxTaskStatusCode.Running, 5), Info(NodeOdxTaskStatusCode.Running, 5), Info(NodeOdxTaskStatusCode.Running, 5),
             Info(NodeOdxTaskStatusCode.Running, 5), Info(NodeOdxTaskStatusCode.Completed, 100)],
            [["[INFO]    Running dataset stage", "[INFO]    Running split stage", "[INFO]    Running merge stage"],
             ["[INFO]    Running opensfm stage"], ["running \"/bin/opensfm/bin/opensfm\" detect_features"], [], []]);
        var reports = new Reports();

        using (await runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, reports, CancellationToken.None))
        {
        }

        var opensfm = reports.All.Where(r => r.Phase == "opensfm").ToList();
        opensfm.Count.ShouldBe(3);
        opensfm.ShouldAllBe(r => r.Fraction <= 0.0475 + 1e-9);
    }

    // ---- FR-PRC-36 ----

    [Test]
    public async Task AFailedNodeTask_IsRemovedFromTheNode_AfterItsOutputIsInTheLog()
    {
        var (runner, client) = Runner(
            [Info(NodeOdxTaskStatusCode.Running, 10), Info(NodeOdxTaskStatusCode.Failed, 10, "Cannot process dataset")],
            [[], ["Traceback: out of memory"]]);
        var reports = new Reports();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, reports, CancellationToken.None));

        ex.Message.ShouldBe("NodeODX task failed: Cannot process dataset");
        reports.All.Select(r => r.LogChunk).ShouldContain("Traceback: out of memory");
        client.Verify(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ACanceledRun_CancelsAndRemovesItsNodeTask_Once()
    {
        var (runner, client) = Runner([Info(NodeOdxTaskStatusCode.Running, 10)], [[]]);
        using var cts = new CancellationTokenSource();
        client.Setup(c => c.GetTaskOutputAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<NodeOdxEndpoint, string, int, CancellationToken>((_, _, _, _) =>
            {
                cts.Cancel();
                return Task.FromResult<IReadOnlyList<string>>([]);
            });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), cts.Token));

        client.Verify(c => c.CancelTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ACanceledRun_CopiesTheOutputSinceTheLastPoll_AfterTheCancel_BeforeTheRemove()
    {
        var (runner, client) = Runner([Info(NodeOdxTaskStatusCode.Running, 10), Info(NodeOdxTaskStatusCode.Running, 20)], []);
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        var outputCalls = 0;
        client.Setup(c => c.GetTaskOutputAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<NodeOdxEndpoint, string, int, CancellationToken>((_, _, since, token) =>
            {
                calls.Add($"output from {since}");
                switch (++outputCalls)
                {
                    case 1:
                        return Task.FromResult<IReadOnlyList<string>>(["line 0", "line 1"]);
                    case 2:
                        // The user cancels while the second poll reads the output
                        cts.Cancel();
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult<IReadOnlyList<string>>([]);
                    default:
                        return Task.FromResult<IReadOnlyList<string>>(["line 2", "Aborted"]);
                }
            });
        client.Setup(c => c.CancelTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("cancel")).Returns(Task.CompletedTask);
        client.Setup(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("remove")).Returns(Task.CompletedTask);
        var reports = new Reports();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, reports, cts.Token));

        calls.ShouldBe(["output from 0", "output from 2", "cancel", "output from 2", "remove"]);
        reports.All.Select(r => r.LogChunk).Where(l => l is "line 0" or "line 1" or "line 2" or "Aborted")
            .ShouldBe(["line 0", "line 1", "line 2", "Aborted"]);
    }

    [Test]
    public async Task ANodeThatTimesOut_FailsTheRun_WithoutCancelingOrRemovingTheNodeTask()
    {
        var (runner, client) = Runner([Info(NodeOdxTaskStatusCode.Running, 10)], [[]]);
        client.Setup(c => c.GetTaskInfoAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."));

        var ex = await Should.ThrowAsync<TimeoutException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));

        ex.Message.ShouldBe("NodeODX node 'n1' did not answer in time.");
        client.Verify(c => c.CancelTaskAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.RemoveTaskAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ANodeThatTimesOutOnTheSubmit_FailsTheRun_NotACancel()
    {
        var (runner, client) = Runner([], []);
        client.Setup(c => c.CreateTaskAsync(It.IsAny<NodeOdxEndpoint>(), "run", It.IsAny<IReadOnlyList<string>>(), null,
            It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException("timeout"));

        await Should.ThrowAsync<TimeoutException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));
    }

    [Test]
    public async Task ATaskCanceledOnTheNode_EndsCanceled_AndIsRemoved_WithoutAnotherCancel()
    {
        var (runner, client) = Runner([Info(NodeOdxTaskStatusCode.Running, 10), Info(NodeOdxTaskStatusCode.Canceled, 10)], [[], []]);

        // (Should.ThrowAsync reports a canceled task as its own TaskCanceledException: the exception is caught here)
        OperationCanceledException? ex = null;
        try
        {
            using var _ = await runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None);
        }
        catch (OperationCanceledException e)
        {
            ex = e;
        }

        ex.ShouldNotBeNull().Message.ShouldBe("NodeODX task was canceled.");
        client.Verify(c => c.CancelTaskAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static readonly NodeOdxTaskInfo[] Done = [Info(NodeOdxTaskStatusCode.Completed, 100)];

    [Test]
    public async Task ADownloadThatFails_RemovesTheNodeTask_Once()
    {
        var (runner, client) = Runner(Done, [[]]);
        client.Setup(c => c.DownloadAssetAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", "all.zip", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("NodeODX asset download 'all.zip' failed (500): boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));

        ex.Message.ShouldBe("NodeODX asset download 'all.zip' failed (500): boom");
        client.Verify(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AnEmptyBundle_RemovesTheNodeTask_Once()
    {
        var (runner, client) = Runner(Done, [[]]);
        client.Setup(c => c.DownloadAssetAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", "all.zip", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<NodeOdxEndpoint, string, string, string, CancellationToken>((_, _, _, path, _) => System.IO.File.WriteAllBytesAsync(path, []));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));

        ex.Message.ShouldBe("NodeODX produced no downloadable result bundle.");
        client.Verify(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ACancelWhileTheBundleDownloads_RemovesTheNodeTask_Once()
    {
        var (runner, client) = Runner(Done, [[]]);
        using var cts = new CancellationTokenSource();
        client.Setup(c => c.DownloadAssetAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", "all.zip", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<NodeOdxEndpoint, string, string, string, CancellationToken>((_, _, _, _, token) =>
            {
                cts.Cancel();
                return Task.FromCanceled(token);
            });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), cts.Token));

        client.Verify(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ADownloadThatTimesOut_FailsTheRun_AndRemovesTheNodeTask()
    {
        var (runner, client) = Runner(Done, [[]]);
        client.Setup(c => c.DownloadAssetAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", "all.zip", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timeout"));

        await Should.ThrowAsync<TimeoutException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));

        client.Verify(c => c.RemoveTaskAsync(Node, "uuid-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ADownloadThatFails_KeepsTheNodeTask_WhenTheOperatorKeepsIt()
    {
        var (runner, client) = Runner(Done, [[]], keepFailed: true);
        client.Setup(c => c.DownloadAssetAsync(It.IsAny<NodeOdxEndpoint>(), "uuid-1", "all.zip", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));

        client.Verify(c => c.RemoveTaskAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AFailedNodeTask_StaysOnTheNode_WhenTheOperatorKeepsIt()
    {
        var (runner, client) = Runner([Info(NodeOdxTaskStatusCode.Failed, 10, "boom")], [[]], keepFailed: true);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));

        client.Verify(c => c.RemoveTaskAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ANodeThatCannotRemoveTheTask_DoesNotHideTheFailure()
    {
        var (runner, client) = Runner([Info(NodeOdxTaskStatusCode.Failed, 10, "boom")], [[]]);
        client.Setup(c => c.RemoveTaskAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.Net.Http.HttpRequestException("gone"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            runner.RunAsync(Node, "run", ["a.jpg", "b.jpg"], null, new Reports(), CancellationToken.None));

        ex.Message.ShouldBe("NodeODX task failed: boom");
    }

    // ---- FR-DEV-10 ----

    [Test]
    public async Task TheOptions_HaveTheirChoicesAndDefaultWrittenIntoTheHelp()
    {
        var client = new Mock<INodeOdxClient>();
        client.Setup(c => c.GetOptionsAsync(It.IsAny<NodeOdxEndpoint>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new NodeOdxOption("feature-quality", "enum", new[] { "ultra", "high", "medium" },
                "Quality: %(choices)s. Default: %(default)s", "high"),
            new NodeOdxOption("orthophoto-resolution", "float", "cm/pixel", "Resolution (default %(default)s) {default}", 5.0),
            new NodeOdxOption("dsm", "bool", null, "Build a DSM: %(default)s", false),
            new NodeOdxOption("name", "string", null, null, null)
        ]);
        var catalog = new ProcessingNodeCatalog(
            new NodeOdxNodeRegistry(Microsoft.Extensions.Options.Options.Create(new ProcessingPlatformSettings
                { NodeOdx = [new NodeOdxNodeConfig { Id = "n1", Url = "http://n1:3000" }] })),
            client.Object, NullLogger<ProcessingNodeCatalog>.Instance);

        var options = await catalog.GetOptionsAsync("n1", CancellationToken.None);

        options.Select(o => o.Help).ShouldBe(
        [
            "Quality: ultra, high, medium. Default: high",
            "Resolution (default 5) 5",
            "Build a DSM: false",
            null
        ]);
        options[0].Value.ShouldBe("high");
    }

    [TestCase("Overlap in %% (default %(default)s)", "Overlap in % (default 5)")]
    [TestCase("Literal %%(default)s stays", "Literal %(default)s stays")]
    [TestCase("100%% means all", "100% means all")]
    public void PythonsEscapedPercent_IsAPercentInTheHelp(string help, string expected) =>
        ProcessingNodeCatalog.FillHelp(new NodeOdxOption("x", "float", null, help, 5.0)).ShouldBe(expected);

    // ---- FR-PRC-42 ----

    private static JobIndex Job(string id, string state, string? path = "a.tif", string tool = "build") => new()
    {
        JobId = id, OrgSlug = "org", DsSlug = "ds", ToolId = tool, ToolVersion = "1", CurrentState = state, Path = path,
        CreatedAtUtc = DateTime.UtcNow, LastStateChangeUtc = DateTime.UtcNow, ProcessingAtUtc = DateTime.UtcNow
    };

    private static TaskSummaryDto Row(string id, string state, string tool = "build", string? phase = null) =>
        new(id, tool, "1", state, null, phase, DateTime.UtcNow, null, null, "b.tif", null, null, null, null, null);

    [Test]
    public void AnEnqueuedBuild_SaysWhichBuildItWaitsBehind()
    {
        var waiting = TaskHistory.WaitingBehind(Row("q", "Enqueued"), Job("big", "Processing", "orthos/big.tif"));

        waiting.PhaseMessage.ShouldBe("Waiting behind orthos/big.tif");
    }

    [Test]
    public void TheOtherRows_KeepTheirMessage()
    {
        var running = Job("big", "Processing", "orthos/big.tif");

        TaskHistory.WaitingBehind(Row("q", "Enqueued", phase: "queued"), running).PhaseMessage.ShouldBe("queued");
        TaskHistory.WaitingBehind(Row("q", "Processing"), running).PhaseMessage.ShouldBeNull();
        TaskHistory.WaitingBehind(Row("q", "Enqueued", tool: "archive-extract"), running).PhaseMessage.ShouldBeNull();
        TaskHistory.WaitingBehind(Row("big", "Enqueued"), running).PhaseMessage.ShouldBeNull();
        TaskHistory.WaitingBehind(Row("q", "Enqueued"), null).PhaseMessage.ShouldBeNull();
    }
}

internal static class SetupExtensions
{
    public static TResult Pipe<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
