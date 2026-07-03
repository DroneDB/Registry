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
using Registry.Web.Services.HeavyTasks.NodeOdm;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Remote photogrammetry tool backed by a NodeODM (OpenDroneMap) processing node.
/// Collects the dataset's images, submits them to NodeODM, streams progress/log,
/// downloads the result bundle (<c>all.zip</c>) and extracts it directly into the
/// dataset (or into a newly created dataset). Extracted files are indexed and
/// per-file build jobs are enqueued automatically. No downloadable artifact is produced.
/// Cooperatively cancellable - cancellation propagates to the remote NodeODM task.
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
            "options": { "type": ["array", "null"], "description": "NodeODM options array [{name,value}]." },
            "destPath": { "type": ["string", "null"], "description": "Destination folder within the dataset for extracted results (required unless createNewDataset is true)." },
            "createNewDataset": { "type": ["boolean", "null"], "description": "If true, create a new dataset for the photogrammetry results instead of extracting into the current dataset." },
            "newDatasetName": { "type": ["string", "null"], "description": "Slug for the new dataset (kebab-case, max 128 chars). Auto-generated if not provided." },
            "newDatasetOrgSlug": { "type": ["string", "null"], "description": "Organization slug for the new dataset (default: same as source dataset)." },
            "newDatasetVisibility": { "type": ["string", "null"], "description": "Visibility for the new dataset: PRIVATE, UNLISTED, or PUBLIC (default: PRIVATE)." }
          },
          "additionalProperties": false
        }
        """);

    // Files are indexed in batches of this size: bounds the per-transaction lock time.
    private const int IndexChunkSize = 250;

    private readonly INodeOdmClient _client;
    private readonly INodeOdmNodeRegistry _nodes;
    private readonly IArchiveExtractor _extractor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessingPlatformSettings _settings;
    private readonly ILogger<PhotogrammetryTool> _logger;
    private readonly TimeSpan _pollInterval;

    public PhotogrammetryTool(
        INodeOdmClient client,
        INodeOdmNodeRegistry nodes,
        IArchiveExtractor extractor,
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> appSettings,
        ILogger<PhotogrammetryTool> logger)
    {
        _client = client;
        _nodes = nodes;
        _extractor = extractor;
        _scopeFactory = scopeFactory;
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
        _logger = logger;
        var seconds = appSettings.Value.ProcessingPlatform?.RemoteNodePollIntervalSeconds ?? 2;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    public string Id => "photogrammetry";
    public string Version => "2";
    public string Title => "Photogrammetry (NodeODM)";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public bool ProducesArtifact => false;
    public JsonDocument InputSchema => Schema;

    public async Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        if (!_nodes.HasNodes)
            throw new InvalidOperationException("No NodeODM processing node is configured.");

        var nodeId = ReadString(request.Params, "nodeId");
        if (_nodes.Resolve(nodeId) is null)
            throw new ArgumentException($"NodeODM node '{nodeId}' is not configured.");

        // Validate explicit image list before collecting.
        var explicitImages = ReadStringArray(request.Params, "images");
        if (explicitImages is { Count: > 0 })
        {
            var missing = explicitImages.Where(p => !ctx.Ddb.EntryExists(p)).ToList();
            if (missing.Count > 0)
                throw new ArgumentException(
                    $"The following image paths do not exist in the dataset: {string.Join(", ", missing.Select(p => $"'{p}'"))}");
        }

        var images = CollectImageEntries(request, ctx.Ddb);
        if (images.Count < 2)
            throw new ArgumentException("Photogrammetry requires at least 2 images.");

        // Validate output destination parameters.
        var createNewDataset = ReadBool(request.Params, "createNewDataset") ?? false;
        var destPath = ReadString(request.Params, "destPath") ?? string.Empty;

        if (createNewDataset)
        {
            // Validate new dataset name if provided.
            var newDatasetName = ReadString(request.Params, "newDatasetName");
            if (string.IsNullOrWhiteSpace(newDatasetName))
                throw new ArgumentException("A new dataset name (slug) is required when createNewDataset is true.");

            if (newDatasetName!.Length > 128)
                throw new ArgumentException("The new dataset name must be 128 characters or less.");

            // Basic kebab-case validation.
            if (!Regex.IsMatch(newDatasetName, @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$"))
                throw new ArgumentException("The new dataset name must be kebab-case (lowercase letters, digits, and hyphens).");

            // Validate visibility.
            var visibilityStr = ReadString(request.Params, "newDatasetVisibility") ?? "PRIVATE";
            if (!Enum.TryParse<Visibility>(visibilityStr, true, out _))
                throw new ArgumentException($"Invalid visibility '{visibilityStr}'. Must be PRIVATE, UNLISTED, or PUBLIC.");
        }
        else
        {
            // destPath is required when not creating a new dataset.
            if (string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("A destination folder path (destPath) is required unless createNewDataset is true.");

            // Destination path safety.
            CommonUtils.ValidateRelativePath(destPath, ctx.Ddb.DatasetFolderPath);
            if (IsReservedPath(destPath))
                throw new ArgumentException($"'{destPath}' is a reserved path.");

            var destEntry = ctx.Ddb.GetEntry(destPath);
            if (destEntry != null && destEntry.Type != EntryType.Directory)
                throw new ArgumentException($"The destination '{destPath}' is not a folder.");
        }
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

        return new HeavyToolPlan(estimate, QuotaKey: "archive-extract",
            DefaultFileName: null, ContentType: null);
    }

    public async Task<HeavyToolArtifact?> ExecuteAsync(
        HeavyToolRequest request,
        IHeavyToolExecutionContext ctx,
        IProgress<HeavyToolProgress> progress,
        CancellationToken ct)
    {
        // --- Phase 1: Submit to NodeODM and download results ---
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

        // Download all.zip to a temp location for extraction.
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"photogrammetry_{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report(new HeavyToolProgress(0.95, "downloading", LogChunk: "Downloading result bundle (all.zip)"));
            await _client.DownloadAssetAsync(node, uuid, "all.zip", tempZipPath, ct);

            var info = new FileInfo(tempZipPath);
            if (!info.Exists || info.Length == 0)
                throw new InvalidOperationException("NodeODM produced no downloadable result bundle.");

            // Best-effort remote cleanup.
            await _client.RemoveTaskAsync(node, uuid, CancellationToken.None);

            // --- Phase 2: Determine output target ---
            var createNewDataset = ReadBool(request.Params, "createNewDataset") ?? false;
            var targetDdb = ctx.Ddb;
            var targetRoot = ctx.Ddb.DatasetFolderPath;
            var targetOrgSlug = request.OrgSlug;
            var targetDsSlug = request.DsSlug;
            var destPath = ReadString(request.Params, "destPath") ?? string.Empty;

            if (createNewDataset)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(new HeavyToolProgress(0.96, "creating-dataset",
                    LogChunk: "Creating new dataset for photogrammetry results"));

                var newDatasetName = ReadString(request.Params, "newDatasetName")
                    ?? $"photogrammetry-{DateTime.UtcNow:yyyyMMddHHmmss}";
                var newDatasetOrgSlug = ReadString(request.Params, "newDatasetOrgSlug") ?? request.OrgSlug;
                var visibilityStr = ReadString(request.Params, "newDatasetVisibility") ?? "PRIVATE";
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
                    targetRoot = targetDdb.DatasetFolderPath;
                }

                // For a new dataset, extract to root (no subfolder).
                destPath = string.Empty;
            }

            // --- Phase 3: Extract archive into target dataset ---
            await ExtractArchiveIntoDataset(
                tempZipPath, destPath, targetDdb, targetRoot, targetOrgSlug, targetDsSlug,
                request, ctx.TaskId, progress, ct);

            progress.Report(new HeavyToolProgress(1, "done",
                LogChunk: $"Photogrammetry complete. Results extracted to '{destPath}' in dataset '{targetDsSlug}'."));

            return null;
        }
        finally
        {
            // Clean up temp zip file.
            try
            {
                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete temporary zip file '{Path}'", tempZipPath);
            }
        }
    }

    /// <summary>
    /// Extracts a downloaded archive into a dataset, indexes files, and enqueues build jobs.
    /// Reuses the same pattern as ArchiveExtractTool.
    /// </summary>
    private async Task ExtractArchiveIntoDataset(
        string archivePath, string destPath, IDDB ddb, string root,
        string orgSlug, string dsSlug,
        HeavyToolRequest request, string taskId,
        IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        var done = 0;
        var extracted = 0;
        var skipped = 0;
        int total;
        var extractedPaths = new List<string>();

        var budget = new ExtractionBudget(_settings.MaxArchiveExtractSizeBytes, root, _settings.DiskSafetyMarginBytes);

        using (var session = _extractor.Open(archivePath))
        {
            total = session.FastFileEntryCount ?? 0;

            progress.Report(new HeavyToolProgress(total > 0 ? 0.96 : -1, "extracting",
                LogChunk: total > 0
                    ? $"Extracting {total} file(s) from photogrammetry results"
                    : $"Extracting photogrammetry results"));

            foreach (var archiveEntry in session.Entries())
            {
                ct.ThrowIfCancellationRequested();
                if (archiveEntry.IsDirectory) continue;

                // Path sanitization (anti zip-slip + reserved-folder guard).
                var target = SafeJoin(destPath, archiveEntry.Key);
                CommonUtils.ValidateRelativePath(target, root);

                // Skip existing files (no overwrite for photogrammetry output).
                if (ddb.EntryExists(target))
                {
                    skipped++;
                    done++;
                    ReportProgress(progress, done, total, archiveEntry.Key, 0.96);
                    continue;
                }

                var localTarget = ddb.GetLocalPath(target);
                var parent = Path.GetDirectoryName(localTarget);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                extractedPaths.Add(target);

                try
                {
                    await using var sourceStream = archiveEntry.OpenStream();
                    await using var fileStream = File.Create(localTarget);
                    await budget.CopyGuardedAsync(sourceStream, fileStream, ct);
                }
                catch (QuotaExceededException)
                {
                    progress.Report(new HeavyToolProgress(-1, "cleanup",
                        LogChunk: $"Out of budget - rolling back {extractedPaths.Count} extracted file(s)"));
                    CleanupExtracted(ddb, extractedPaths);
                    throw;
                }

                extracted++;
                done++;
                ReportProgress(progress, done, total, archiveEntry.Key, 0.96);
            }
        }

        ct.ThrowIfCancellationRequested();

        // Index everything in batches.
        var indexed = new List<Entry>(extractedPaths.Count);
        for (var i = 0; i < extractedPaths.Count; i += IndexChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = extractedPaths.GetRange(i, Math.Min(IndexChunkSize, extractedPaths.Count - i));
            indexed.AddRange(ddb.AddRawBatch(slice));
            progress.Report(new HeavyToolProgress(
                extractedPaths.Count > 0 ? 0.97 + 0.02 * (double)(i + slice.Count) / extractedPaths.Count : -1,
                "indexing", LogChunk: $"Indexed {i + slice.Count}/{extractedPaths.Count} file(s)"));
        }

        // Enqueue per-file build jobs for buildable entries.
        using (var scope = _scopeFactory.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobsProcessor>();
            var buildableCount = 0;

            foreach (var entry in indexed)
            {
                if (!ddb.IsBuildable(entry.Path))
                    continue;

                var path = entry.Path;
                var meta = new IndexPayload(
                    orgSlug,
                    dsSlug,
                    entry.Hash,
                    null,
                    Path: path,
                    ParentJobId: taskId);

                Expression<Action> buildJob = () => HangfireUtils.BuildWrapper(ddb, path, false, null);
                processor.EnqueueIndexed(buildJob, meta);
                buildableCount++;
            }

            _logger.LogInformation("Enqueued {Count} build job(s) for photogrammetry results in {Org}/{Ds}",
                buildableCount, orgSlug, dsSlug);
        }

        // Invalidate cached tiles/thumbnails/OGC.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cacheInvalidator = scope.ServiceProvider.GetRequiredService<IDatasetCacheInvalidator>();
            await cacheInvalidator.InvalidateAllDatasetCachesAsync(orgSlug, dsSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache invalidation after photogrammetry extract failed for {Org}/{Ds}",
                orgSlug, dsSlug);
        }

        progress.Report(new HeavyToolProgress(0.99, "extracting",
            LogChunk: $"Extraction complete: {extracted} extracted, {skipped} skipped"));
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
                    progress.Report(new HeavyToolProgress(fraction * 0.95, phase, LogChunk: line));
            }
            else
            {
                progress.Report(new HeavyToolProgress(fraction * 0.95, phase));
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

    private static string SafeJoin(string destPath, string entryKey)
    {
        var key = (entryKey ?? string.Empty).Replace('\\', '/').Trim();
        while (key.StartsWith('/'))
            key = key[1..];

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Archive entry has an empty name.");

        if (Path.IsPathRooted(key) || key.Split('/').Any(seg => seg == ".."))
            throw new InvalidOperationException($"Unsafe archive entry path (zip-slip): '{entryKey}'.");

        var combined = CommonUtils.SafeCombine(destPath ?? string.Empty, key);

        if (IsReservedPath(combined))
            throw new InvalidOperationException($"Archive entry targets a reserved path: '{combined}'.");

        return combined;
    }

    private static bool IsReservedPath(string path)
        => path.Replace('\\', '/').TrimStart('/').StartsWith(IDDB.DatabaseFolderName, StringComparison.Ordinal);

    private static void ReportProgress(IProgress<HeavyToolProgress> progress, int done, int total, string currentKey, double baseFraction)
    {
        var fraction = total > 0 ? baseFraction + 0.02 * (double)done / total : -1;
        progress.Report(new HeavyToolProgress(fraction, "extracting", Message: currentKey));
    }

    private void CleanupExtracted(IDDB ddb, List<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                var local = ddb.GetLocalPath(p);
                if (File.Exists(local))
                    File.Delete(local);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete partially extracted file '{Path}'", p);
            }
        }
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool? ReadBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
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
}
