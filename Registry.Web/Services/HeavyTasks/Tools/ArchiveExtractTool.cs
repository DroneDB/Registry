#nullable enable
using System;
using System.IO;
using System.Linq;
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
/// re-indexes it with <c>AddRaw</c>, then builds the pending derivatives inline
/// (mirrors <c>RescanIndexTool</c> / <c>MergeMultispectralTool</c>). Mutates the
/// dataset in place, so it produces no downloadable artifact.
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

        // Destination path safety (no traversal, not under the reserved .ddb folder).
        if (!string.IsNullOrEmpty(destPath))
        {
            CommonUtils.ValidateRelativePath(destPath, ctx.Ddb.DatasetFolderPath);
            if (IsReservedPath(destPath))
                throw new ArgumentException($"'{destPath}' is a reserved path.");
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
            uncompressed = session.TotalUncompressedBytes;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"The archive could not be opened: {ex.Message}");
        }

        if (uncompressed is > 0)
        {
            // 1) User storage quota — same guard used by single-file uploads.
            //    Resolved through a child scope because the tool is a singleton; the
            //    current user is read from IHttpContextAccessor (valid at submit time).
            using (var scope = _scopeFactory.CreateScope())
            {
                var utils = scope.ServiceProvider.GetRequiredService<IUtils>();
                await utils.CheckCurrentUserStorage(uncompressed.Value); // throws QuotaExceededException
            }

            // 2) Free disk space on the dataset volume (best-effort).
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
                estimate = session.TotalUncompressedBytes;
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

        using var session = _extractor.Open(localArchive);
        var total = session.FileEntryCount;
        var done = 0;
        var extracted = 0;
        var skipped = 0;

        progress.Report(new HeavyToolProgress(total > 0 ? 0 : -1, "extracting",
            LogChunk: $"Extracting {total} file(s) from '{sourcePath}'"));

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

            // File.Create truncates an existing file -> honors overwrite=true.
            await using (var sourceStream = archiveEntry.OpenStream())
            await using (var fileStream = File.Create(localTarget))
                await sourceStream.CopyToAsync(fileStream, ct);

            ctx.Ddb.AddRaw(target);
            extracted++;
            done++;
            ReportProgress(progress, done, total, archiveEntry.Key);
        }

        ct.ThrowIfCancellationRequested();

        // Build the derivatives for the newly indexed (pending) files inline, so the
        // task only completes once everything is ready (RescanIndexTool pattern).
        progress.Report(new HeavyToolProgress(total > 0 ? 1 : -1, "building",
            LogChunk: "Building derivatives"));
        HangfireUtils.BuildPendingWrapper(ctx.Ddb, ctx.Hangfire);

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

        // Invalidate cached tiles/thumbnails/OGC (no auth needed; keyed by org/ds).
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var objects = scope.ServiceProvider.GetRequiredService<IObjectsManager>();
            await objects.InvalidateAllDatasetCaches(request.OrgSlug, request.DsSlug);
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
