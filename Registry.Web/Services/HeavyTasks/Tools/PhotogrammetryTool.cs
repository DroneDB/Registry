#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.NodeOdm;
using Registry.Web.Services.HeavyTasks.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Remote photogrammetry tool backed by a NodeODM (OpenDroneMap) processing node.
/// Collects the dataset's images, submits them to NodeODM, streams progress/log,
/// downloads the result bundle (<c>all.zip</c>) as the task artifact. Cooperatively
/// cancellable - cancellation propagates to the remote NodeODM task.
/// </summary>
public sealed class PhotogrammetryTool : IHeavyTool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "folder": { "type": ["string", "null"], "description": "Dataset folder to scan for images (default: whole dataset)." },
            "images": { "type": ["array", "null"], "items": { "type": "string" }, "description": "Explicit list of image entry paths." },
            "nodeId": { "type": ["string", "null"], "description": "Target NodeODM node id (default: first configured)." },
            "name": { "type": ["string", "null"], "description": "Task name shown on the node." },
            "fileName": { "type": ["string", "null"], "description": "Output bundle file name." },
            "options": { "type": ["array", "null"], "description": "NodeODM options array [{name,value}]." }
          },
          "additionalProperties": false
        }
        """);

    private readonly INodeOdmClient _client;
    private readonly INodeOdmNodeRegistry _nodes;
    private readonly TimeSpan _pollInterval;

    public PhotogrammetryTool(
        INodeOdmClient client,
        INodeOdmNodeRegistry nodes,
        IOptions<AppSettings> appSettings)
    {
        _client = client;
        _nodes = nodes;
        var seconds = appSettings.Value.ProcessingPlatform?.RemoteNodePollIntervalSeconds ?? 2;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    public string Id => "photogrammetry";
    public string Version => "1";
    public string Title => "Photogrammetry (NodeODM)";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Read;
    public bool ProducesArtifact => true;
    public JsonDocument InputSchema => Schema;

    public Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        if (!_nodes.HasNodes)
            throw new InvalidOperationException("No NodeODM processing node is configured.");

        var nodeId = ReadString(request.Params, "nodeId");
        if (_nodes.Resolve(nodeId) is null)
            throw new ArgumentException($"NodeODM node '{nodeId}' is not configured.");

        var images = CollectImageEntries(request, ctx.Ddb);
        
        return images.Count < 2 ? throw new ArgumentException("Photogrammetry requires at least 2 images.") : Task.CompletedTask;
    }

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var images = CollectImageEntries(request, ctx.Ddb);
            var inputBytes = images.Sum(e => Math.Max(0, e.Size));
            // ODM products (ortho + DSM/DTM + point cloud + model) are typically a
            // fraction of the raw image payload; rough upper-bound heuristic.
            if (inputBytes > 0) estimate = (long)(inputBytes * 0.75);
        }
        catch
        {
            // best-effort estimate
        }

        var fileName = ResolveFileName(request);
        return new HeavyToolPlan(estimate, QuotaKey: "photogrammetry", DefaultFileName: fileName,
            ContentType: "application/zip");
    }

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        if (ctx.WorkDir is null)
            throw new InvalidOperationException("PhotogrammetryTool requires a work directory.");

        var nodeId = ReadString(request.Params, "nodeId");
        var node = _nodes.Resolve(nodeId)
                   ?? throw new InvalidOperationException($"NodeODM node '{nodeId}' is not configured.");

        var entries = CollectImageEntries(request, ctx.Ddb);
        if (entries.Count < 2)
            throw new InvalidOperationException("Photogrammetry requires at least 2 images.");

        var imagePaths = entries.Select(e => ctx.Ddb.GetLocalPath(e.Path)).ToList();
        var taskName = ReadString(request.Params, "name") ?? $"{request.OrgSlug}/{request.DsSlug}";
        var optionsJson = ReadRawJsonArray(request.Params, "options");

        progress.Report(new HeavyToolProgress(0, "submitting",
            LogChunk: $"Submitting {imagePaths.Count} images to NodeODM node '{node.Id}'"));

        var uuid = await _client.CreateTaskAsync(node, taskName, imagePaths, optionsJson, ct);
        progress.Report(new HeavyToolProgress(0, "queued", LogChunk: $"NodeODM task created: {uuid}"));

        try
        {
            await PollUntilDoneAsync(node, uuid, progress, ct);
        }
        catch (OperationCanceledException)
        {
            await _client.CancelTaskAsync(node, uuid, CancellationToken.None);
            throw;
        }

        var fileName = ResolveFileName(request);
        var outputPath = Path.Combine(ctx.WorkDir, fileName);

        progress.Report(new HeavyToolProgress(0.98, "downloading", LogChunk: "Downloading result bundle (all.zip)"));
        await _client.DownloadAssetAsync(node, uuid, "all.zip", outputPath, ct);

        var info = new FileInfo(outputPath);
        if (!info.Exists || info.Length == 0)
            throw new InvalidOperationException("NodeODM produced no downloadable result bundle.");

        var sha = await ComputeSha256Async(outputPath, ct);

        // Best-effort remote cleanup; the node also expires the workspace on its own.
        await _client.RemoveTaskAsync(node, uuid, CancellationToken.None);

        progress.Report(new HeavyToolProgress(1, "done", LogChunk: $"Photogrammetry complete ({info.Length} bytes)"));

        return new HeavyToolArtifact(
            RelativePath: fileName,
            ContentType: "application/zip",
            FileName: fileName,
            SizeBytes: info.Length,
            Sha256: sha);
    }

    private async Task PollUntilDoneAsync(
        NodeOdmEndpoint node, string uuid, IProgress<HeavyToolProgress> progress, CancellationToken ct)
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
                    progress.Report(new HeavyToolProgress(fraction, phase, LogChunk: line));
            }
            else
            {
                progress.Report(new HeavyToolProgress(fraction, phase));
            }

            switch (info.StatusCode)
            {
                case NodeOdmTaskStatusCode.Completed:
                    return;
                case NodeOdmTaskStatusCode.Failed:
                    throw new InvalidOperationException(
                        $"NodeODM task failed: {info.ErrorMessage ?? "unknown error"}");
                case NodeOdmTaskStatusCode.Canceled:
                    throw new OperationCanceledException("NodeODM task was canceled.");
            }

            await Task.Delay(_pollInterval, ct);
        }
    }

    private static string PhaseFor(NodeOdmTaskStatusCode code) => code switch
    {
        NodeOdmTaskStatusCode.Queued => "queued",
        NodeOdmTaskStatusCode.Running => "processing",
        NodeOdmTaskStatusCode.Completed => "completed",
        NodeOdmTaskStatusCode.Failed => "failed",
        NodeOdmTaskStatusCode.Canceled => "canceled",
        _ => "processing"
    };

    private static List<Entry> CollectImageEntries(HeavyToolRequest request, IDDB ddb)
    {
        var explicitImages = ReadStringArray(request.Params, "images");
        IEnumerable<Entry> entries;

        if (explicitImages is { Count: > 0 })
        {
            entries = explicitImages
                .Select(ddb.GetEntry)
                .Where(e => e is not null)
                .Select(e => e!);
        }
        else
        {
            var folder = ReadString(request.Params, "folder") ?? request.Path ?? string.Empty;
            entries = ddb.Search(folder, recursive: true);
        }

        return entries
            .Where(e => e.Type is EntryType.Image or EntryType.GeoImage)
            .ToList();
    }

    private static string ResolveFileName(HeavyToolRequest request)
    {
        var name = ReadString(request.Params, "fileName");
        if (!string.IsNullOrWhiteSpace(name)) return name!;
        return "photogrammetry_result.zip";
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static List<string>? ReadStringArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        return el.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToList();
    }

    private static string? ReadRawJsonArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        return el.GetRawText();
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
