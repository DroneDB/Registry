#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// Native tool that extracts a compressed archive stored in a dataset, adding each
/// extracted file to the dataset index exactly as if it had been uploaded
/// individually (spec ExtractArchive). Runs on the Hangfire worker (HTTP-context
/// free) and works entirely through <see cref="IDDB"/>: it writes each entry to disk,
/// re-indexes it with <c>AddRaw</c>, then enqueues a per-file build job for every
/// buildable entry (mirrors <c>ObjectsManager.AddNew</c>). Mutates the dataset in
/// place, so it produces no downloadable artifact.
/// </summary>
public sealed class ArchiveExtractTool : IHeavyTool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse(
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["sourcePath"],
          "properties": {
            "sourcePath":    { "type": "string", "minLength": 1, "title": "Archive" },
            "destPath":      { "type": "string", "default": "", "title": "Extract to" },
            "deleteArchive": { "type": "boolean", "default": false, "title": "Delete archive after extraction" },
            "overwrite":     { "type": "boolean", "default": false, "title": "Overwrite existing files" }
          },
          "additionalProperties": false
        }
        """);

    private readonly IArchiveExtractor _extractor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessingPlatformSettings _settings;
    private readonly ILogger<ArchiveExtractTool> _logger;

    public ArchiveExtractTool(
        IArchiveExtractor extractor,
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> appSettings,
        ILogger<ArchiveExtractTool> logger)
    {
        _extractor = extractor;
        _scopeFactory = scopeFactory;
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
        _logger = logger;
    }

    public string Id => "archive-extract";
    public string Version => "1";
    public string Title => "Extract archive";
    public HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public bool ProducesArtifact => false;
    public JsonDocument InputSchema => Schema;

    // Files are indexed in batches of this size: bounds the per-transaction lock time and
    // lets progress be reported between chunks while still collapsing the per-file native
    // database opens into one-per-chunk.
    private const int IndexChunkSize = 250;

    public async Task ValidateAsync(HeavyToolRequest request, IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var sourcePath = ReadString(request.Params, "sourcePath");
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A source archive path is required.");

        var destPath = ReadString(request.Params, "destPath") ?? string.Empty;

        // Source must exist, be a file (not a directory) and a supported archive.
        var entry = ctx.Ddb.GetEntry(sourcePath)
                    ?? throw new ArgumentException($"Archive '{sourcePath}' was not found in the dataset.");
        if (entry.Type == EntryType.Directory)
            throw new ArgumentException("The source path is a folder, not an archive.");
        if (!_extractor.IsSupported(sourcePath))
            throw new ArgumentException($"'{sourcePath}' is not a supported archive format.");

        // Destination path safety (no traversal, not under the reserved .ddb folder,
        // and not targeting an existing non-folder entry).
        if (!string.IsNullOrEmpty(destPath))
        {
            CommonUtils.ValidateRelativePath(destPath, ctx.Ddb.DatasetFolderPath);
            if (IsReservedPath(destPath))
                throw new ArgumentException($"'{destPath}' is a reserved path.");

            var destEntry = ctx.Ddb.GetEntry(destPath);
            if (destEntry != null && destEntry.Type != EntryType.Directory)
                throw new ArgumentException($"The destination '{destPath}' is not a folder.");
        }

        // --- Size / quota / disk-space guards ---
        var localArchive = ctx.Ddb.GetLocalPath(sourcePath);
        var archiveSize = entry.Size > 0
            ? entry.Size
            : (File.Exists(localArchive) ? new FileInfo(localArchive).Length : 0);

        if (_settings.MaxArchiveExtractSizeBytes > 0 && archiveSize > _settings.MaxArchiveExtractSizeBytes)
            throw new ArgumentException(
                $"The archive is too large to extract ({CommonUtils.GetBytesReadable(archiveSize)}). " +
                $"The maximum allowed size is {CommonUtils.GetBytesReadable(_settings.MaxArchiveExtractSizeBytes)}.");

        long? uncompressed;
        try
        {
            using var session = _extractor.Open(localArchive);
            uncompressed = session.FastUncompressedBytes;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"The archive could not be opened: {ex.Message}");
        }

        if (uncompressed is > 0)
        {
            // The uncompressed size is known cheaply only for random-access formats
            // (zip/rar/7z/tar). When available we enforce the user quota + disk space here
            // at submit. For compressed tarballs the size is null (computing it would
            // require fully decompressing the archive), so those guards run incrementally
            // during extraction instead - see ExecuteAsync.
            if (ctx.Caller is not null)
            {
                using var scope = _scopeFactory.CreateScope();
                var utils = scope.ServiceProvider.GetRequiredService<IUtils>();
                await utils.CheckCurrentUserStorage(uncompressed.Value); // throws QuotaExceededException
            }

            // Free disk space on the dataset volume (best-effort). Runs in both
            // contexts: at execution time it is the worker's disk that matters.
            EnsureDiskSpace(ctx.Ddb.DatasetFolderPath, uncompressed.Value);
        }
    }

    public HeavyToolPlan Plan(HeavyToolRequest request, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var sourcePath = ReadString(request.Params, "sourcePath");
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                var localArchive = ctx.Ddb.GetLocalPath(sourcePath);
                using var session = _extractor.Open(localArchive);
                estimate = session.FastUncompressedBytes;
            }
        }
        catch
        {
            // estimate is best-effort
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
        var sourcePath = ReadString(request.Params, "sourcePath")
                         ?? throw new InvalidOperationException("A source archive path is required.");
        var destPath = ReadString(request.Params, "destPath") ?? string.Empty;
        var deleteArchive = ReadBool(request.Params, "deleteArchive") ?? false;
        var overwrite = ReadBool(request.Params, "overwrite") ?? false;

        var localArchive = ctx.Ddb.GetLocalPath(sourcePath);
        var root = ctx.Ddb.DatasetFolderPath;
        var done = 0;
        var extracted = 0;
        var skipped = 0;
        int total;
        var extractedPaths = new List<string>();

        // For compressed tarballs the uncompressed size cannot be known without fully
        // decompressing the archive (which we deliberately avoid), so instead of a
        // submit-time guard we extract in a single streaming pass and enforce the space
        // budget incrementally. If we run out of room we roll back everything extracted in
        // this run and fail with a clear "not enough space" message. Indexing happens only
        // AFTER the loop, so on abort nothing has been added to the index yet - we just
        // delete the files written so far.
        // Cap (primary) + disk head-room (secondary, re-sampled) consolidated in ExtractionBudget.
        var budget = new ExtractionBudget(_settings.MaxArchiveExtractSizeBytes, root, _settings.DiskSafetyMarginBytes);

        // The session (and its underlying file handle) is closed as soon as the
        // extraction loop finishes, BEFORE indexing and deleteArchive. This is critical:
        // keeping the session open through deleteArchive causes the OS to refuse the
        // File.Delete because the handle is still held.
        using (var session = _extractor.Open(localArchive))
        {
            // Null (=> 0) for compressed tarballs: the count is unknown without
            // decompressing, so the progress bar is indeterminate during extraction.
            total = session.FastFileEntryCount ?? 0;

            progress.Report(new HeavyToolProgress(total > 0 ? 0 : -1, "extracting",
                LogChunk: total > 0
                    ? $"Extracting {total} file(s) from '{sourcePath}'"
                    : $"Extracting '{sourcePath}'"));

            foreach (var archiveEntry in session.Entries())
            {
                ct.ThrowIfCancellationRequested();
                if (archiveEntry.IsDirectory) continue;

                // Path sanitization (anti zip-slip + reserved-folder guard).
                var target = SafeJoin(destPath, archiveEntry.Key);
                CommonUtils.ValidateRelativePath(target, root); // defense in depth

                // Overwrite / skip semantics.
                if (ctx.Ddb.EntryExists(target) && !overwrite)
                {
                    skipped++;
                    done++;
                    ReportProgress(progress, done, total, archiveEntry.Key);
                    continue;
                }

                var localTarget = ctx.Ddb.GetLocalPath(target);
                var parent = Path.GetDirectoryName(localTarget);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                // Track the (possibly partial) file BEFORE writing so a rollback removes it too.
                extractedPaths.Add(target);

                // Stream the entry through the shared budget: the cap (primary) and disk head-room
                // (secondary) are enforced BEFORE each chunk, so an under-reported or oversized entry
                // cannot fill the volume mid-copy. File.Create truncates -> honors overwrite=true.
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
                    CleanupExtracted(ctx, extractedPaths);
                    throw;
                }

                extracted++;
                done++;
                ReportProgress(progress, done, total, archiveEntry.Key);
            }
        } // ← session.Dispose() - file handle released here

        ct.ThrowIfCancellationRequested();

        // Index everything in batches: one DDB transaction (and one native connection
        // open) per chunk instead of one-per-file. The returned entries carry Type + Hash,
        // so the buildable files can be scheduled without any extra per-file
        // IsBuildable/GetEntry calls (each of which would re-open the native database).
        var indexed = new List<Entry>(extractedPaths.Count);
        for (var i = 0; i < extractedPaths.Count; i += IndexChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var slice = extractedPaths.GetRange(i, Math.Min(IndexChunkSize, extractedPaths.Count - i));
            indexed.AddRange(ctx.Ddb.AddRawBatch(slice));
            progress.Report(new HeavyToolProgress(
                extractedPaths.Count > 0 ? (double)(i + slice.Count) / extractedPaths.Count : -1,
                "indexing", LogChunk: $"Indexed {i + slice.Count}/{extractedPaths.Count} file(s)"));
        }

        // Enqueue a per-file build job for every buildable extracted entry (mirrors
        // ObjectsManager.AddNew). Buildability is derived from the indexed entry type, so
        // no additional native calls are needed.
        using (var scope = _scopeFactory.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobsProcessor>();
            var buildableCount = 0;

            foreach (var entry in indexed)
            {
                if (!ctx.Ddb.IsBuildable(entry.Path))
                    continue;

                var path = entry.Path;
                var meta = new IndexPayload(
                    request.OrgSlug,
                    request.DsSlug,
                    entry.Hash,
                    null,
                    Path: path,
                    ParentJobId: ctx.TaskId);

                Expression<Action> buildJob = () => HangfireUtils.BuildWrapper(ctx.Ddb, path, false, null);
                processor.EnqueueIndexed(buildJob, meta);
                buildableCount++;
            }

            _logger.LogInformation("Enqueued {Count} build job(s) for extracted files in {Org}/{Ds}",
                buildableCount, request.OrgSlug, request.DsSlug);
        }

        // Optionally remove the source archive (index entry + physical file).
        if (deleteArchive)
        {
            try
            {
                ctx.Ddb.Remove(sourcePath);
                if (File.Exists(localArchive))
                    File.Delete(localArchive);
                progress.Report(new HeavyToolProgress(1, "cleanup",
                    LogChunk: $"Deleted source archive '{sourcePath}'"));
            }
            catch (Exception ex)
            {
                progress.Report(new HeavyToolProgress(1, "cleanup",
                    LogChunk: $"Could not delete source archive '{sourcePath}': {ex.Message}"));
            }
        }

        // Invalidate cached tiles/thumbnails/OGC (no auth needed; keyed by org/ds). Uses the
        // focused IDatasetCacheInvalidator, registered on every host including processing nodes.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cacheInvalidator = scope.ServiceProvider.GetRequiredService<IDatasetCacheInvalidator>();
            await cacheInvalidator.InvalidateAllDatasetCachesAsync(request.OrgSlug, request.DsSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache invalidation after extract failed for {Org}/{Ds}",
                request.OrgSlug, request.DsSlug);
        }

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: $"Extraction complete: {extracted} extracted, {skipped} skipped"));
        return null;
    }

    private void EnsureDiskSpace(string datasetFolderPath, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(datasetFolderPath));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            if (drive.AvailableFreeSpace < requiredBytes)
                throw new QuotaExceededException(
                    "Not enough free disk space to extract the archive. " +
                    $"Required: {CommonUtils.GetBytesReadable(requiredBytes)}, " +
                    $"available: {CommonUtils.GetBytesReadable(drive.AvailableFreeSpace)}.");
        }
        catch (QuotaExceededException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine free disk space for '{Path}'; skipping disk-space guard.",
                datasetFolderPath);
        }
    }

    // Removes the files written during a failed extraction run. Index entries are added
    // only after extraction completes, so on abort there is nothing to un-index.
    private void CleanupExtracted(IHeavyToolExecutionContext ctx, List<string> paths)
    {
        foreach (var p in paths)
        {
            try
            {
                var local = ctx.Ddb.GetLocalPath(p);
                if (File.Exists(local))
                    File.Delete(local);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete partially extracted file '{Path}'", p);
            }
        }
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

    private static void ReportProgress(IProgress<HeavyToolProgress> progress, int done, int total, string currentKey)
    {
        var fraction = total > 0 ? (double)done / total : -1;
        progress.Report(new HeavyToolProgress(fraction, "extracting", Message: currentKey));
    }

    private static bool IsReservedPath(string path)
        => path.Replace('\\', '/').TrimStart('/').StartsWith(IDDB.DatabaseFolderName, StringComparison.Ordinal);

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
}
