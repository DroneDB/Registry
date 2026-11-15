using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Tools;

/// <summary>
/// The last step of a photogrammetry run (<see cref="PhotogrammetryTool"/>): the result bundle a NodeODX node made is
/// extracted into the target dataset through the shared extractor, as the task's own writes (FR-XC-27), without the
/// internal folders of the bundle (FR-PRC-41), then indexed with the builds of its buildable files queued.
/// </summary>
public sealed class PhotogrammetryResultsImport
{
    private readonly DatasetArchiveExtractor _archives;
    private readonly TaskFileIndexer _indexer;
    private readonly ImportSettings _importSettings;
    private readonly ProcessingPlatformSettings _settings;
    private readonly IFileSystem _fs;

    /// <param name="archives">The shared extractor (path rule, budget, rollback of a failed extraction).</param>
    /// <param name="indexer">Indexes the results and queues their builds.</param>
    /// <param name="importSettings">The import settings (the extension policy).</param>
    /// <param name="platformSettings">The processing platform settings (archive limit, disk safety margin, the folders of
    /// the bundle that are not imported).</param>
    /// <param name="fs">The file system the dataset folder is read from.</param>
    public PhotogrammetryResultsImport(DatasetArchiveExtractor archives, TaskFileIndexer indexer,
        IOptions<ImportSettings> importSettings, IOptions<ProcessingPlatformSettings> platformSettings, IFileSystem fs)
    {
        _archives = archives;
        _indexer = indexer;
        _importSettings = importSettings.Value;
        _settings = platformSettings.Value;
        _fs = fs;
    }

    /// <summary>
    /// Extracts the downloaded bundle <paramref name="archivePath"/> into <paramref name="destPath"/> of the dataset
    /// <paramref name="ddb"/>, indexes the files, and enqueues their build jobs.
    /// </summary>
    public async Task ImportAsync(string archivePath, string destPath, IDDB ddb, DatasetKey dataset, string orgSlug,
        string dsSlug, IHeavyToolExecutionContext ctx, IProgress<HeavyToolProgress> progress, CancellationToken ct)
    {
        var root = ddb.DatasetFolderPath;

        // The shared extractor: the path rule decides every entry, the budget every chunk, existing
        // files are kept (no overwrite for photogrammetry output), any failure leaves the dataset as it
        // was, and every result is recorded in the task's write record of the target dataset before it is
        // written (FR-XC-27)
        var writes = await TaskImportWrites.StartAsync(ctx.WriteRecord, dataset, root, _fs, ct);

        // The cap is the smaller of the archive limit and what the owner of the target organization may still store, read
        // now, when the results are written (FR-PRC-08, D-039): when they do not fit the task fails with the 507 text, and
        // the write record rolls back what was written. The internal folders of the bundle are not imported (FR-PRC-41)
        var (maxBytes, maxBytesMessage) = await WorkerStorageQuota.CapAsync(ctx.Storage, orgSlug,
            _settings.MaxArchiveExtractSizeBytes, replacedBytes: null, ct);
        var extraction = await _archives.ExtractAsync(
            new ArchiveExtractionRequest(archivePath, root, destPath, _importSettings.IsExtensionAllowed,
                location => !writes.MayWrite(location.Path), maxBytes,
                _settings.DiskSafetyMarginBytes, writes, Exclude: ExcludedResultEntry(destPath),
                MaxBytesMessage: maxBytesMessage),
            new CallbackProgress<ArchiveExtractionProgress>(p => ReportProgress(progress, p)), ct);
        var extractedPaths = extraction.Extracted.ToList();

        ct.ThrowIfCancellationRequested();

        // Index, queue the builds of the buildable results (children of this task), invalidate the caches
        await _indexer.IndexAsync(new TaskFiles(ddb, dataset, orgSlug, dsSlug, extractedPaths, ctx.BuildQueue, ctx.TaskId,
            "photogrammetry results", ProgressFrom: 0.97, ProgressTo: 0.99), progress, ct);

        progress.Report(new HeavyToolProgress(0.99, "extracting", LogChunk: string.Join('\n', extraction.LogLines())));
    }

    // The entries of the result bundle that are not imported: the dataset path is under the folder the results go to, the
    // prefix is read relative to the bundle (FR-PRC-41)
    private Func<string, bool> ExcludedResultEntry(string destPath)
    {
        var prefixes = _settings.PhotogrammetryExcludedFoldersOrDefault();
        var root = destPath.Length == 0 ? string.Empty : destPath.TrimEnd('/') + "/";
        return target =>
        {
            var inBundle = root.Length > 0 && target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? target[root.Length..]
                : target;
            return prefixes.Any(prefix => !string.IsNullOrEmpty(prefix) &&
                                          inBundle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        };
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
        progress.Report(new HeavyToolProgress(fraction, "extracting", Message: p.Key.Length > 0 ? p.Key : null));
    }

    // Reports inline (System.Progress<T> posts to the thread pool)
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
