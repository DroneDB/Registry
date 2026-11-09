using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// The NodeODX side of a photogrammetry task (out of <c>PhotogrammetryTool</c>): the configured nodes, and
/// one run on a node: submit the images, follow the node's progress and log, download the result bundle.
/// </summary>
public sealed partial class NodeOdxTaskRunner
{
    private readonly INodeOdxClient _client;
    private readonly NodeOdxNodeRegistry _nodes;
    private readonly ScratchFileProvider _scratch;
    private readonly TimeSpan _pollInterval;
    private readonly bool _keepFailedTasks;

    public NodeOdxTaskRunner(INodeOdxClient client, NodeOdxNodeRegistry nodes, ScratchFileProvider scratch,
        IOptions<ProcessingPlatformSettings> platformSettings)
    {
        _client = client;
        _nodes = nodes;
        _scratch = scratch;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, platformSettings.Value.RemoteNodePollIntervalSeconds));
        _keepFailedTasks = platformSettings.Value.KeepFailedNodeTasks;
    }

    /// <summary>Whether any node is configured.</summary>
    public bool HasNodes => _nodes.HasNodes;

    /// <summary>The node <paramref name="nodeId"/> (the first one configured when null), or null when there is none.</summary>
    public NodeOdxEndpoint? Resolve(string? nodeId) => _nodes.Resolve(nodeId);

    /// <summary>
    /// Runs <paramref name="imagePaths"/> on <paramref name="node"/> to the end, reporting the node's progress and log,
    /// and returns the result bundle (<c>all.zip</c>) as a scratch file: the caller disposes it when done. The task
    /// is canceled on the node when <paramref name="ct"/> is, and removed from it once the bundle is downloaded, or when
    /// it failed or was canceled (unless <see cref="ProcessingPlatformSettings.KeepFailedNodeTasks"/>; FR-PRC-36): the
    /// output it printed is in the task's log by then.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node's task failed, or produced no result bundle.</exception>
    /// <exception cref="TimeoutException">The node did not answer a request in time (not a cancel: the node task stays).</exception>
    /// <exception cref="OperationCanceledException">The task was canceled (here or on the node).</exception>
    public async Task<ScratchFile> RunAsync(NodeOdxEndpoint node, string taskName, IReadOnlyList<string> imagePaths,
        string? optionsJson, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        progress.Report(new HeavyToolProgress(0, "submitting",
            LogChunk: $"Submitting {imagePaths.Count} images to NodeODX node '{node.Id}'"));

        string uuid;
        try
        {
            uuid = await _client.CreateTaskAsync(node, taskName, imagePaths, optionsJson, ct);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw NoAnswer(node, ex);
        }
        progress.Report(new HeavyToolProgress(0, "queued", LogChunk: $"NodeODX task created: {uuid}",
            WaitingForNode: true));

        var poll = new PollState();
        try
        {
            await PollUntilDoneAsync(node, uuid, poll, progress, ct);
        }
        catch (NodeTaskCanceledException)
        {
            // Canceled on the node: its output is in the log already
            await RemoveUnlessKeptAsync(node, uuid);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Canceled here: the node stops the task, and what it printed since the last poll goes to the log first
            await CancelOnNodeAsync(node, uuid);
            await CopyLastOutputAsync(node, uuid, poll, progress);
            await RemoveUnlessKeptAsync(node, uuid);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // A request of the node timed out: a failure, not a cancel. The node task is left as it is
            throw NoAnswer(node, ex);
        }
        catch (NodeTaskFailedException)
        {
            await RemoveUnlessKeptAsync(node, uuid);
            throw;
        }

        // Download all.zip to the configured temp folder for extraction; the caller deletes it at the end
        var bundle = _scratch.Create("photogrammetry_", ".zip");
        try
        {
            progress.Report(new HeavyToolProgress(0.95, "downloading", LogChunk: "Downloading result bundle (all.zip)"));
            try
            {
                await _client.DownloadAssetAsync(node, uuid, "all.zip", bundle.Path, ct);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw NoAnswer(node, ex);
            }

            var info = new FileInfo(bundle.Path);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("NodeODX produced no downloadable result bundle.");

            // Best-effort remote cleanup.
            await RemoveAsync(node, uuid);
            return bundle;
        }
        catch
        {
            // The download failed, the bundle is empty, or the run was canceled while it downloaded
            bundle.Dispose();
            await RemoveUnlessKeptAsync(node, uuid);
            throw;
        }
    }

    /// <summary>The node's task failed: its output is in the log already; the task is removed from the node.</summary>
    private sealed class NodeTaskFailedException(string message) : InvalidOperationException(message);

    /// <summary>The node says its task was canceled (there, not here): the run ends canceled.</summary>
    private sealed class NodeTaskCanceledException(string message) : OperationCanceledException(message);

    /// <summary>A request of the node timed out (<see cref="System.Net.Http.HttpClient.Timeout"/>): the run fails.</summary>
    private static TimeoutException NoAnswer(NodeOdxEndpoint node, Exception inner) =>
        new($"NodeODX node '{node.Id}' did not answer in time.", inner);

    /// <summary>Where the poll of a run is: the output lines read so far, and the last progress reported.</summary>
    private sealed class PollState
    {
        public int OutputLine;
        public double Fraction;
        public string Phase = "queued";
    }

    /// <summary>
    /// Removes the task of a failed or canceled run from the node, best effort: the run already ends with its own
    /// error, and a node that cannot be reached cannot be cleaned either.
    /// </summary>
    private Task RemoveUnlessKeptAsync(NodeOdxEndpoint node, string uuid) =>
        _keepFailedTasks ? Task.CompletedTask : RemoveAsync(node, uuid);

    /// <summary>Removes the task from the node, best effort.</summary>
    private async Task RemoveAsync(NodeOdxEndpoint node, string uuid)
    {
        try
        {
            await _client.RemoveTaskAsync(node, uuid, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort: a node that cannot be reached cannot be cleaned
        }
    }

    /// <summary>Asks the node to stop the task, best effort: the run ends canceled either way.</summary>
    private async Task CancelOnNodeAsync(NodeOdxEndpoint node, string uuid)
    {
        try
        {
            await _client.CancelTaskAsync(node, uuid, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort (see above)
        }
    }

    /// <summary>
    /// Copies the output the node printed since the last poll to the log (once, after a cancel; FR-PRC-36), best
    /// effort: the run ends canceled either way.
    /// </summary>
    private async Task CopyLastOutputAsync(NodeOdxEndpoint node, string uuid, PollState poll, IProgress<HeavyToolProgress> progress)
    {
        try
        {
            var lines = await _client.GetTaskOutputAsync(node, uuid, poll.OutputLine, CancellationToken.None);
            poll.OutputLine += lines.Count;
            foreach (var line in lines)
                progress.Report(new HeavyToolProgress(poll.Fraction, poll.Phase, LogChunk: line));
        }
        catch (Exception)
        {
            // Best effort (see above)
        }
    }

    private async Task PollUntilDoneAsync(NodeOdxEndpoint node, string uuid, PollState poll,
        IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        var stage = (string?)null;
        var stageIndex = -1;
        double? lastNodeProgress = null;
        var reported = 0.0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var info = await _client.GetTaskInfoAsync(node, uuid, ct);
            var fraction = Math.Clamp(info.Progress / 100.0, 0, 1);
            var waiting = info.StatusCode == NodeOdxTaskStatusCode.Queued;

            var lines = await _client.GetTaskOutputAsync(node, uuid, poll.OutputLine, ct);
            poll.OutputLine += lines.Count;
            foreach (var line in lines)
                if (StageOf(line) is { } found && (found.Explicit || found.Index > stageIndex))
                {
                    stage = found.Label;
                    stageIndex = found.Index >= 0 ? found.Index : stageIndex;
                }

            // The node's percent can stay the same through a long stage: the stage then says how far the run is, at
            // least (a floor: the percent ODX itself sends when the stage starts, so never ahead of the node; the
            // percent of the node wins once it moves on; the bar never goes back)
            if (stageIndex >= 0 && lastNodeProgress == info.Progress)
                fraction = Math.Max(fraction, Stages[stageIndex].StartPercent / 100.0);
            lastNodeProgress = info.Progress;
            fraction = Math.Max(fraction, reported);
            reported = fraction;

            // The task waits while the node keeps it queued; the first other status starts its processing (D-024)
            var phase = info.StatusCode == NodeOdxTaskStatusCode.Running && stage != null
                ? stage
                : PhaseFor(info.StatusCode);
            poll.Fraction = fraction * 0.95;
            poll.Phase = phase;

            if (lines.Count > 0)
            {
                foreach (var line in lines)
                    progress.Report(new HeavyToolProgress(fraction * 0.95, phase, LogChunk: line,
                        WaitingForNode: waiting));
            }
            else
            {
                progress.Report(new HeavyToolProgress(fraction * 0.95, phase, WaitingForNode: waiting));
            }

            switch (info.StatusCode)
            {
                case NodeOdxTaskStatusCode.Completed:
                    return;
                case NodeOdxTaskStatusCode.Failed:
                    throw new NodeTaskFailedException(
                        $"NodeODX task failed: {info.ErrorMessage ?? "unknown error"}");
                case NodeOdxTaskStatusCode.Canceled:
                    throw new NodeTaskCanceledException("NodeODX task was canceled.");
            }

            await Task.Delay(_pollInterval, ct);
        }
    }

    /// <summary>
    /// The stages of an ODX run in the order it runs them (the stage names of its log), with the word each is shown by,
    /// and the percent ODX has sent when the stage starts. From ODX's <c>stages/odm_app.py</c>: each stage ends at its
    /// <c>progress</c> (dataset 5, opensfm 25, openmvs 50, odm_filterpoints 52, odm_meshing 60, mvs_texturing 70,
    /// odm_georeferencing 80, odm_dem 90, odm_orthophoto 98, odm_report 99) and starts where the stage before it
    /// ended. Where the pipeline varies, the lowest start: split and merge end at 0 on a normal dataset (so opensfm
    /// starts at dataset's 5), and odm_filterpoints follows opensfm (25) when --fast-orthophoto skips openmvs.
    /// </summary>
    private static readonly (string Name, string Label, double StartPercent)[] Stages =
    [
        ("dataset", "dataset", 0), ("split", "split", 5), ("merge", "merge", 5), ("opensfm", "opensfm", 5),
        ("openmvs", "openmvs", 25), ("odm_filterpoints", "filtering points", 25), ("odm_meshing", "meshing", 52),
        ("mvs_texturing", "texturing", 60), ("odm_georeferencing", "georeferencing", 70), ("odm_dem", "DEM", 80),
        ("odm_orthophoto", "orthophoto", 90), ("odm_report", "report", 98), ("odm_postprocess", "postprocess", 99)
    ];

    /// <summary>
    /// The stage a line of the ODX output starts: "Running opensfm stage", or "running ... opensfm/openmvs/odm_meshing/..."
    /// of the tools it starts; null for any other line. Index is the position in <see cref="Stages"/>, -1 for a stage it
    /// does not list (shown by its name). Explicit: the line names the stage; the other kind names a tool a stage
    /// starts, which a later stage may start again (opensfm exports), so it never takes the run back.
    /// </summary>
    internal static (string Label, int Index, bool Explicit)? StageOf(string line)
    {
        var match = StageStart().Match(line);
        if (!match.Success) return null;
        var explicitStage = match.Groups["stage"].Success;
        var name = (explicitStage ? match.Groups["stage"] : match.Groups["tool"]).Value.ToLowerInvariant();
        if (name == "odm_25dmeshing") name = "odm_meshing";
        var index = Array.FindIndex(Stages, s => s.Name == name);
        return index >= 0
            ? (Stages[index].Label, index, explicitStage)
            : (name.StartsWith("odm_", StringComparison.Ordinal) ? name[4..] : name, -1, explicitStage);
    }

    [GeneratedRegex(
        @"\brunning\s+(?<stage>\w+)\s+stage\b|\brunning\b.*?\b(?<tool>opensfm|openmvs|odm_meshing|mvs_texturing|odm_dem|odm_orthophoto)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StageStart();

    private static string PhaseFor(NodeOdxTaskStatusCode code) => code switch
    {
        NodeOdxTaskStatusCode.Queued => "queued",
        NodeOdxTaskStatusCode.Running => "processing",
        NodeOdxTaskStatusCode.Completed => "completed",
        NodeOdxTaskStatusCode.Failed => "failed",
        NodeOdxTaskStatusCode.Canceled => "canceled",
        _ => "processing"
    };
}
