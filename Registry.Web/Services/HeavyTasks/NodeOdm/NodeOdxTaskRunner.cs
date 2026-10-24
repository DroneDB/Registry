#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.NodeOdx;

/// <summary>
/// The NodeODX side of a photogrammetry task (RQ-12, out of <c>PhotogrammetryTool</c>): the configured nodes, and
/// one run on a node: submit the images, follow the node's progress and log, download the result bundle.
/// </summary>
public interface INodeOdxTaskRunner
{
    /// <summary>Whether any node is configured.</summary>
    bool HasNodes { get; }

    /// <summary>The node <paramref name="nodeId"/> (the first one configured when null), or null when there is none.</summary>
    NodeOdxEndpoint? Resolve(string? nodeId);

    /// <summary>
    /// Runs <paramref name="imagePaths"/> on <paramref name="node"/> to the end, reporting the node's progress and log,
    /// and returns the result bundle (<c>all.zip</c>) as a scratch file: the caller disposes it when done. The task
    /// is canceled on the node when <paramref name="ct"/> is, and removed from it once the bundle is downloaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">The node's task failed, or produced no result bundle.</exception>
    /// <exception cref="OperationCanceledException">The task was canceled (here or on the node).</exception>
    Task<ScratchFile> RunAsync(NodeOdxEndpoint node, string taskName, IReadOnlyList<string> imagePaths,
        string? optionsJson, IProgress<HeavyToolProgress> progress, CancellationToken ct);
}

/// <summary>Default <see cref="INodeOdxTaskRunner"/>.</summary>
public sealed class NodeOdxTaskRunner : INodeOdxTaskRunner
{
    private readonly INodeOdxClient _client;
    private readonly INodeOdxNodeRegistry _nodes;
    private readonly IScratchFileProvider _scratch;
    private readonly TimeSpan _pollInterval;

    public NodeOdxTaskRunner(INodeOdxClient client, INodeOdxNodeRegistry nodes, IScratchFileProvider scratch,
        IOptions<ProcessingPlatformSettings> platformSettings)
    {
        _client = client;
        _nodes = nodes;
        _scratch = scratch;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, platformSettings.Value.RemoteNodePollIntervalSeconds));
    }

    public bool HasNodes => _nodes.HasNodes;

    public NodeOdxEndpoint? Resolve(string? nodeId) => _nodes.Resolve(nodeId);

    public async Task<ScratchFile> RunAsync(NodeOdxEndpoint node, string taskName, IReadOnlyList<string> imagePaths,
        string? optionsJson, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        progress.Report(new HeavyToolProgress(0, "submitting",
            LogChunk: $"Submitting {imagePaths.Count} images to NodeODX node '{node.Id}'"));

        var uuid = await _client.CreateTaskAsync(node, taskName, imagePaths, optionsJson, ct);
        progress.Report(new HeavyToolProgress(0, "queued", LogChunk: $"NodeODX task created: {uuid}"));

        try
        {
            await PollUntilDoneAsync(node, uuid, progress, ct);
        }
        catch (OperationCanceledException)
        {
            await _client.CancelTaskAsync(node, uuid, CancellationToken.None);
            throw;
        }

        // Download all.zip to the configured temp folder for extraction; the caller deletes it at the end
        var bundle = _scratch.Create("photogrammetry_", ".zip");
        try
        {
            progress.Report(new HeavyToolProgress(0.95, "downloading", LogChunk: "Downloading result bundle (all.zip)"));
            await _client.DownloadAssetAsync(node, uuid, "all.zip", bundle.Path, ct);

            var info = new FileInfo(bundle.Path);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("NodeODX produced no downloadable result bundle.");

            // Best-effort remote cleanup.
            await _client.RemoveTaskAsync(node, uuid, CancellationToken.None);
            return bundle;
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    private async Task PollUntilDoneAsync(
        NodeOdxEndpoint node, string uuid, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        var outputLine = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var info = await _client.GetTaskInfoAsync(node, uuid, ct);
            var fraction = Math.Clamp(info.Progress / 100.0, 0, 1);
            var phase = PhaseFor(info.StatusCode);

            var lines = await _client.GetTaskOutputAsync(node, uuid, outputLine, ct);
            if (lines.Count > 0)
            {
                outputLine += lines.Count;
                foreach (var line in lines)
                    progress.Report(new HeavyToolProgress(fraction * 0.95, phase, LogChunk: line));
            }
            else
            {
                progress.Report(new HeavyToolProgress(fraction * 0.95, phase));
            }

            switch (info.StatusCode)
            {
                case NodeOdxTaskStatusCode.Completed:
                    return;
                case NodeOdxTaskStatusCode.Failed:
                    throw new InvalidOperationException(
                        $"NodeODX task failed: {info.ErrorMessage ?? "unknown error"}");
                case NodeOdxTaskStatusCode.Canceled:
                    throw new OperationCanceledException("NodeODX task was canceled.");
            }

            await Task.Delay(_pollInterval, ct);
        }
    }

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
