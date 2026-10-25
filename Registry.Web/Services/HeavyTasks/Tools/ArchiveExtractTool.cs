#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports.DroneDB;
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
public sealed class ArchiveExtractTool : HeavyToolBase<ArchiveExtractTool.Parameters>
{
    /// <summary>The tool's parameters as the request carries them (absent or ill-typed: null).</summary>
    public sealed record Parameters(string? SourcePath, string? DestPath, bool? DeleteArchive, bool? Overwrite);

    protected override Parameters ReadParams(ToolParameters p) => new(
        p.Text("sourcePath"),
        p.Text("destPath"),
        p.Flag("deleteArchive"),
        p.Flag("overwrite"));


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

    private readonly DatasetArchiveExtractor _archives;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessingPlatformSettings _settings;
    private readonly ImportSettings _importSettings;
    private readonly DatasetPathPolicy _paths;
    private readonly VolumeHeadroom _headroom;
    private readonly TaskFileIndexer _indexer;

    public ArchiveExtractTool(
        DatasetArchiveExtractor archives,
        IServiceScopeFactory scopeFactory,
        IOptions<ImportSettings> importSettings, IOptions<ProcessingPlatformSettings> platformSettings,
        DatasetPathPolicy paths,
        VolumeHeadroom headroom,
        TaskFileIndexer indexer)
    {
        _headroom = headroom;
        _indexer = indexer;
        _archives = archives;
        _paths = paths;
        _scopeFactory = scopeFactory;
        _settings = platformSettings.Value;
        _importSettings = importSettings.Value;
    }

    public override string Id => "archive-extract";
    public override string Version => "1";
    public override string Title => "Extract archive";
    public override HeavyToolPermission RequiredAccess => HeavyToolPermission.Write;
    public override bool ProducesArtifact => false;
    public override JsonDocument InputSchema => Schema;

    protected override async Task ValidateAsync(HeavyToolRequest request, Parameters args,
        IHeavyToolValidationContext ctx, CancellationToken ct)
    {
        var sourcePath = args.SourcePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("A source archive path is required.");
        sourcePath = _paths.Normalize(sourcePath, "archive path");

        // Destination: libddb's path rule ("" is the dataset root)
        var destPath = _paths.NormalizeFolder(args.DestPath);

        // Source must exist, be a file (not a directory) and a supported archive.
        var entry = ctx.Ddb.GetEntry(sourcePath)
                    ?? throw new ArgumentException($"Archive '{sourcePath}' was not found in the dataset.");
        if (entry.Type == EntryType.Directory)
            throw new ArgumentException("The source path is a folder, not an archive.");
        if (!_archives.IsSupported(sourcePath))
            throw new ArgumentException($"'{sourcePath}' is not a supported archive format.");

        // The destination may not be an existing non-folder entry
        if (destPath.Length > 0)
        {
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
            uncompressed = _archives.FastUncompressedBytes(localArchive);
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
                var quota = scope.ServiceProvider.GetRequiredService<IStorageQuota>();
                await quota.CheckCurrentUserStorage(uncompressed.Value); // throws QuotaExceededException
            }

            // Free disk space on the dataset volume, keeping the head-room (the safety margin plus
            // libddb's free-disk floor). Runs in both contexts: at execution time it is the worker's
            // disk that matters.
            _headroom.EnsureFree(ctx.Ddb.DatasetFolderPath, uncompressed.Value, _settings.DiskSafetyMarginBytes);
        }
    }

    protected override HeavyToolPlan Plan(HeavyToolRequest request, Parameters args, IHeavyToolValidationContext ctx)
    {
        long? estimate = null;
        try
        {
            var sourcePath = args.SourcePath;
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                estimate = _archives.FastUncompressedBytes(ctx.Ddb.GetLocalPath(sourcePath));
            }
        }
        catch
        {
            // estimate is best-effort
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
        var sourcePath = _paths.Normalize(args.SourcePath, "archive path");
        var destPath = _paths.NormalizeFolder(args.DestPath);
        var deleteArchive = args.DeleteArchive ?? false;
        var overwrite = args.Overwrite ?? false;

        var localArchive = ctx.Ddb.GetLocalPath(sourcePath);

        // One streaming pass through the shared extractor: the path rule decides every entry, the
        // budget (cap, then disk head-room with libddb's floor) every chunk - compressed tarballs have
        // no cheap uncompressed size, so the submit-time guard cannot cover them - and any failure
        // leaves the dataset as it was. Indexing happens only AFTER the extraction, so a rollback has
        // nothing to un-index. The archive is closed before indexing and deleteArchive.
        var extraction = await _archives.ExtractAsync(
            new ArchiveExtractionRequest(localArchive, ctx.Ddb.DatasetFolderPath, destPath,
                _importSettings.IsExtensionAllowed,
                target => !overwrite && ctx.Ddb.EntryExists(target),
                _settings.MaxArchiveExtractSizeBytes, _settings.DiskSafetyMarginBytes),
            new CallbackProgress<ArchiveExtractionProgress>(p => ReportProgress(progress, p, sourcePath)), ct);
        var extractedPaths = extraction.Extracted.ToList();

        ct.ThrowIfCancellationRequested();

        // Index, queue the builds of the buildable files (children of this task), invalidate the caches
        await _indexer.IndexAsync(new TaskFiles(ctx.Ddb, ctx.Dataset, request.OrgSlug, request.DsSlug,
            extractedPaths, ctx.BuildQueue, ctx.TaskId, "extracted files"), progress, ct);

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

        progress.Report(new HeavyToolProgress(1, "done",
            LogChunk: $"Extraction complete: {extraction.Extracted.Count} extracted, {extraction.Skipped} skipped"));
        return null;
    }

    private static void ReportProgress(IProgress<HeavyToolProgress> progress, ArchiveExtractionProgress p, string sourcePath)
    {
        if (p.Done == 0)
        {
            // Total 0: a compressed tarball, the count is unknown without decompressing (indeterminate bar)
            progress.Report(new HeavyToolProgress(p.Total > 0 ? 0 : -1, "extracting",
                LogChunk: p.Total > 0 ? $"Extracting {p.Total} file(s) from '{sourcePath}'" : $"Extracting '{sourcePath}'"));
            return;
        }

        progress.Report(new HeavyToolProgress(p.Total > 0 ? (double)p.Done / p.Total : -1, "extracting", Message: p.Key));
    }

    // Reports inline (System.Progress<T> posts to the thread pool)
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }


}
