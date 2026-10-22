#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.NodeOdx;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Import;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Remote photogrammetry tool backed by a NodeODX (OpenDroneMap) processing node.
/// Collects the dataset's images, submits them to NodeODX, streams progress/log,
/// downloads the result bundle (<c>all.zip</c>) and extracts it directly into the
/// dataset (or into a newly created dataset). Extracted files are indexed and
/// per-file build jobs are enqueued automatically. No downloadable artifact is produced.
/// Cooperatively cancellable - cancellation propagates to the remote NodeODX task.
/// </summary>
public sealed class PhotogrammetryTool : HeavyToolBase<PhotogrammetryTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? NodeId, string? Folder, List<string>? Images, bool? CreateNewDataset, string? DestPath, string? NewDatasetName, string? NewDatasetOrgSlug, string? NewDatasetVisibility, string? Name, string? Options);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("nodeId"),
        p.Text("folder"),
        p.Strings("images"),
        p.Flag("createNewDataset"),
        p.Text("destPath"),
        p.Text("newDatasetName"),
        p.Text("newDatasetOrgSlug"),
        p.Text("newDatasetVisibility"),
        p.Text("name"),
        p.RawArray("options"));


    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "folder": { "type": ["string", "null"], "description": "Dataset folder to scan for images (default: whole dataset)." },
            "images": { "type": ["array", "null"], "items": { "type": "string" }, "description": "Explicit list of image entry paths." },
            "nodeId": { "type": ["string", "null"], "description": "Target NodeODX node id (default: first configured)." },
            "name": { "type": ["string", "null"], "description": "Task name shown on the node." },
            "options": { "type": ["array", "null"], "description": "NodeODX options array [{name,value}]." },
            "destPath": { "type": ["string", "null"], "description": "Destination folder within the dataset for extracted results (required unless createNewDataset is true)." },
            "createNewDataset": { "type": ["boolean", "null"], "description": "If true, create a new dataset for the photogrammetry results instead of extracting into the current dataset." },
            "newDatasetName": { "type": ["string", "null"], "description": "Slug for the new dataset (kebab-case, max 128 chars). Auto-generated if not provided." },
            "newDatasetOrgSlug": { "type": ["string", "null"], "description": "Organization slug for the new dataset (default: same as source dataset)." },
            "newDatasetVisibility": { "type": ["string", "null"], "description": "Visibility for the new dataset: PRIVATE, UNLISTED, or PUBLIC (default: PRIVATE)." }
          },
          "additionalProperties": false
        }
        """);

    private readonly INodeOdxClient _client;
    private readonly INodeOdxNodeRegistry _nodes;
    private readonly DatasetArchiveExtractor _archives;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessingPlatformSettings _settings;
    private readonly ImportSettings _importSettings;
    private readonly DatasetPathPolicy _paths;
    private readonly IScratchFileProvider _scratch;
    private readonly TaskFileIndexer _indexer;
    private readonly TimeSpan _pollInterval;

    public PhotogrammetryTool(
        INodeOdxClient client,
        INodeOdxNodeRegistry nodes,
        DatasetArchiveExtractor archives,
        IServiceScopeFactory scopeFactory,
        IOptions<ImportSettings> importSettings, IOptions<ProcessingPlatformSettings> platformSettings,
        DatasetPathPolicy paths,
        IScratchFileProvider scratch,
        TaskFileIndexer indexer)
    {
        _scratch = scratch;
        _client = client;
        _paths = paths;
        _nodes = nodes;
        _archives = archives;
        _scopeFactory = scopeFactory;
        _settings = platformSettings.Value;
        _importSettings = importSettings.Value;
        _indexer = indexer;
        var seconds = platformSettings.Value.RemoteNodePollIntervalSeconds;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    public override string Id => "photogrammetry";
    public override string Version => "2";
    public override string Title => "Photogrammetry (NodeODX)";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override JsonDocument InputSchema => Schema;

    protected override async Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        if (!_nodes.HasNodes)
            throw new InvalidOperationException("No NodeODX processing node is configured.");

        var nodeId = args.NodeId;
        if (_nodes.Resolve(nodeId) is null)
            throw new ArgumentException($"NodeODX node '{nodeId}' is not configured.");

        // Validate the optional image folder path (empty = whole dataset). Reject
        // rooted paths and traversal so a clean dataset-relative path is required.
        _paths.NormalizeFolder(args.Folder);

        // Validate explicit image list before collecting.
        var explicitImages = args.Images;
        if (explicitImages is { Count: > 0 })
        {
            var missing = explicitImages.Where(p => !ctx.Ddb.EntryExists(_paths.Normalize(p))).ToList();
            if (missing.Count > 0)
                throw new ArgumentException(
                    $"The following image paths do not exist in the dataset: {string.Join(", ", missing.Select(p => $"'{p}'"))}");
        }

        var images = CollectImageEntries(request, args, ctx.Ddb);
        if (images.Count < 2)
            throw new ArgumentException("Photogrammetry requires at least 2 images.");

        // Validate output destination parameters.
        var createNewDataset = args.CreateNewDataset ?? false;
        var destPath = args.DestPath ?? string.Empty;

        if (createNewDataset)
        {
            // Validate new dataset name when explicitly provided; omit to auto-generate a timestamped slug.
            var newDatasetName = args.NewDatasetName;
            if (!string.IsNullOrWhiteSpace(newDatasetName))
            {
                if (newDatasetName.Length > 128)
                    throw new ArgumentException("The new dataset name must be 128 characters or less.");

                // Basic kebab-case validation.
                if (!Regex.IsMatch(newDatasetName, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$"))
                    throw new ArgumentException("The new dataset name must be kebab-case (lowercase letters, digits, and hyphens).");
            }

            // Validate visibility.
            var visibilityStr = args.NewDatasetVisibility ?? "PRIVATE";
            if (!Enum.TryParse<Visibility>(visibilityStr, true, out _))
                throw new ArgumentException($"Invalid visibility '{visibilityStr}'. Must be PRIVATE, UNLISTED, or PUBLIC.");
        }
        else
        {
            // destPath is required when not creating a new dataset.
            if (string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("A destination folder path (destPath) is required unless createNewDataset is true.");

            // libddb's path rule
            destPath = _paths.NormalizeFolder(destPath);

            var destEntry = ctx.Ddb.GetEntry(destPath);
            if (destEntry != null && destEntry.Type != EntryType.Directory)
                throw new ArgumentException($"The destination '{destPath}' is not a folder.");
        }
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var images = CollectImageEntries(request, args, ctx.Ddb);
            var inputBytes = images.Sum(e => Math.Max(0, e.Size));
            // ODM products (ortho + DSM/DTM + point cloud + model) are typically a
            // fraction of the raw image payload; rough upper-bound heuristic.
            if (inputBytes > 0) estimate = (long)(inputBytes * 0.75);
        }
        catch
        {
            // best-effort estimate
        }

        return new HeavyToolPlan(estimate, QuotaKey: "archive-extract",
            DefaultFileName: null, ContentType: null);
    }

    protected override async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        Parameters args,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        // --- Phase 1: Submit to NodeODX and download results ---
        var nodeId = args.NodeId;
        var node = _nodes.Resolve(nodeId)
                   ?? throw new InvalidOperationException($"NodeODX node '{nodeId}' is not configured.");

        var entries = CollectImageEntries(request, args, ctx.Ddb);
        if (entries.Count < 2)
            throw new InvalidOperationException("Photogrammetry requires at least 2 images.");

        var imagePaths = entries.Select(e => ctx.Ddb.GetLocalPath(e.Path)).ToList();
        var taskName = args.Name ?? $"{request.OrgSlug}/{request.DsSlug}";
        var optionsJson = args.Options;

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

        // Download all.zip to the configured temp folder for extraction; deleted at the end
        using var tempZip = _scratch.Create("photogrammetry_", ".zip");
        var tempZipPath = tempZip.Path;
        progress.Report(new HeavyToolProgress(0.95, "downloading", LogChunk: "Downloading result bundle (all.zip)"));
        await _client.DownloadAssetAsync(node, uuid, "all.zip", tempZipPath, ct);

        var info = new FileInfo(tempZipPath);
        if (!info.Exists || info.Length == 0)
            throw new InvalidOperationException("NodeODX produced no downloadable result bundle.");

        // Best-effort remote cleanup.
        await _client.RemoveTaskAsync(node, uuid, CancellationToken.None);

        // --- Phase 2: Determine output target ---
        var createNewDataset = args.CreateNewDataset ?? false;
        var targetDdb = ctx.Ddb;
        var targetDataset = ctx.Dataset;
        var targetRoot = ctx.Ddb.DatasetFolderPath;
        var targetOrgSlug = request.OrgSlug;
        var targetDsSlug = request.DsSlug;
        var destPath = _paths.NormalizeFolder(args.DestPath);

        if (createNewDataset)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new HeavyToolProgress(0.96, "creating-dataset",
                LogChunk: "Creating new dataset for photogrammetry results"));

            var newDatasetName = args.NewDatasetName
                ?? $"photogrammetry-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var newDatasetOrgSlug = args.NewDatasetOrgSlug ?? request.OrgSlug;
            var visibilityStr = args.NewDatasetVisibility ?? "PRIVATE";
            var visibility = Enum.Parse<Visibility>(visibilityStr, true);

            targetOrgSlug = newDatasetOrgSlug;

            // This tool is a singleton; resolve the scoped IDatasetsManager and IDdbManager
            // from a child scope at the point of use.
            using (var scope = _scopeFactory.CreateScope())
            {
                var datasetsManager = scope.ServiceProvider.GetRequiredService<IDatasetsManager>();
                var newDs = await datasetsManager.AddNew(newDatasetOrgSlug, new DatasetNewDto
                {
                    Slug = newDatasetName,
                    Name = newDatasetName.Replace("-", " "),
                    Visibility = visibility,
                    Tagline = "Photogrammetry results"
                });

                targetDsSlug = newDs.Slug;
                progress.Report(new HeavyToolProgress(0.96, "creating-dataset",
                    LogChunk: $"Created new dataset '{newDs.Slug}' in organization '{newDatasetOrgSlug}'"));

                // Resolve the DDB for the new dataset.
                var ddbManager = scope.ServiceProvider.GetRequiredService<IDdbManager>();
                var internalRef = newDs.Properties.TryGetValue("internalRef", out var refVal)
                    ? Guid.Parse(refVal.ToString()!)
                    : throw new InvalidOperationException("New dataset is missing internalRef.");
                targetDdb = ddbManager.Get(newDatasetOrgSlug, internalRef);
                targetDataset = new DatasetKey(newDatasetOrgSlug, internalRef);
                targetRoot = targetDdb.DatasetFolderPath;
            }

            // For a new dataset, extract to root (no subfolder).
            destPath = string.Empty;
        }

        // --- Phase 3: Extract archive into target dataset ---
        await ExtractArchiveIntoDataset(
            tempZipPath, destPath, targetDdb, targetDataset, targetRoot, targetOrgSlug, targetDsSlug,
            request, ctx, progress, ct);

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: $"Photogrammetry complete. Results extracted to '{destPath}' in dataset '{targetDsSlug}'."));

        return null;
    }

    /// <summary>
    /// Extracts a downloaded archive into a dataset, indexes files, and enqueues build jobs.
    /// Reuses the same pattern as ArchiveExtractTool.
    /// </summary>
    private async Task ExtractArchiveIntoDataset(
        string archivePath, string destPath, IDDB ddb, DatasetKey dataset, string root,
        string orgSlug, string dsSlug,
        HeavyToolRequest request, IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        // The shared extractor: the path rule decides every entry, the budget every chunk, existing
        // entries are kept (no overwrite for photogrammetry output) and any failure leaves the
        // dataset as it was
        var extraction = await _archives.ExtractAsync(
            new ArchiveExtractionRequest(archivePath, root, destPath, _importSettings.IsExtensionAllowed,
                ddb.EntryExists, _settings.MaxArchiveExtractSizeBytes, _settings.DiskSafetyMarginBytes),
            new CallbackProgress<ArchiveExtractionProgress>(p => ReportProgress(progress, p)), ct);
        var extractedPaths = extraction.Extracted.ToList();

        ct.ThrowIfCancellationRequested();

        // Index, queue the builds of the buildable results (children of this task), invalidate the caches
        await _indexer.IndexAsync(new TaskFiles(ddb, dataset, orgSlug, dsSlug, extractedPaths, ctx.BuildQueue, ctx.TaskId,
            "photogrammetry results", ProgressFrom: 0.97, ProgressTo: 0.99), progress, ct);

        progress.Report(new HeavyToolProgress(0.99, "extracting",
            LogChunk: $"Extraction complete: {extraction.Extracted.Count} extracted, {extraction.Skipped} skipped"));
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

    private List<Entry> CollectImageEntries(HeavyToolRequest request, Parameters args, IDDB ddb)
    {
        var explicitImages = args.Images;
        IEnumerable<Entry> entries;

        if (explicitImages is { Count: > 0 })
        {
            entries = explicitImages
                .Select(p => ddb.GetEntry(_paths.Normalize(p)))
                .Where(e => e is not null)
                .Select(e => e!);
        }
        else
        {
            var folder = _paths.NormalizeFolder(args.Folder ?? request.Path);
            entries = ddb.Find(EntryQuery.OfTypes(folder, EntryType.Image, EntryType.GeoImage));
        }

        return
        [
            .. entries
                .Where(e => e.Type is EntryType.Image or EntryType.GeoImage)
        ];
    }

    // The extraction is the 0.96..0.98 slice of the task
    private static void ReportProgress(IProgress<HeavyToolProgress> progress, ArchiveExtractionProgress p)
    {
        if (p.Done == 0)
        {
            progress.Report(new HeavyToolProgress(p.Total > 0 ? 0.96 : -1, "extracting",
                LogChunk: p.Total > 0
                    ? $"Extracting {p.Total} file(s) from photogrammetry results"
                    : "Extracting photogrammetry results"));
            return;
        }

        var fraction = p.Total > 0 ? 0.96 + 0.02 * p.Done / p.Total : -1;
        progress.Report(new HeavyToolProgress(fraction, "extracting", Message: p.Key));
    }

    // Reports inline (System.Progress<T> posts to the thread pool)
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }




}
