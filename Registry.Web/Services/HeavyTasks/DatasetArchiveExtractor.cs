#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.Archives;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>What to extract, where, and what to skip.</summary>
/// <param name="ArchivePath">The archive, a local file.</param>
/// <param name="DatasetFolder">The dataset folder (local).</param>
/// <param name="DestinationFolder">The normalized dataset path the entries go under; "" for the dataset root.</param>
/// <param name="IsExtensionAllowed">The import policy: an entry whose extension it refuses is skipped.</param>
/// <param name="KeepExisting">True when the file at this dataset path must be kept (the entry is skipped).</param>
/// <param name="MaxBytes">The uncompressed-size cap; 0 (or less) disables it.</param>
/// <param name="SafetyMarginBytes">The disk head-room to keep (<see cref="VolumeHeadroom.MarginBytes"/>).</param>
public sealed record ArchiveExtractionRequest(
    string ArchivePath,
    string DatasetFolder,
    string DestinationFolder,
    Func<string, bool> IsExtensionAllowed,
    Func<string, bool> KeepExisting,
    long MaxBytes,
    long SafetyMarginBytes);

/// <summary>Progress of an extraction, after each entry (and once before the first, with <see cref="Done"/> 0).</summary>
/// <param name="Done">Entries handled (extracted or skipped).</param>
/// <param name="Total">File entries in the archive; 0 when unknown without decompressing (compressed tarballs).</param>
/// <param name="Key">The name of the entry just handled ("" before the first).</param>
/// <param name="BytesWritten">Bytes extracted so far.</param>
public sealed record ArchiveExtractionProgress(int Done, int Total, string Key, long BytesWritten);

/// <summary>What an extraction wrote.</summary>
/// <param name="Extracted">The dataset paths written, in archive order, each once.</param>
/// <param name="Skipped">Entries skipped: kept existing files, refused extensions, reserved paths, folder entries without a name.</param>
/// <param name="BytesWritten">Bytes extracted.</param>
/// <param name="Total">File entries in the archive (0 when it was unknown).</param>
public sealed record ArchiveExtractionResult(IReadOnlyList<string> Extracted, int Skipped, long BytesWritten, int Total);

/// <summary>
/// The one archive extraction into a dataset (RQ-10), shared by the archive extract tool, the
/// photogrammetry results and the archive URL import (each had its own copy of this loop).
/// <list type="bullet">
/// <item>Every entry goes through the dataset path rule (<see cref="DatasetPathPolicy"/>): an unsafe
/// name fails the extraction (zip-slip: "..", a drive, UNC or device prefix, a NUL byte), a reserved
/// path (".ddb", the root ".uploads") is skipped, every written file is inside the dataset.</item>
/// <item>Every byte goes through the budget (<see cref="VolumeHeadroom.CreateBudget"/>): the cap, then
/// the disk head-room with libddb's free-disk floor.</item>
/// <item>All or nothing: until the last entry is written nothing is committed. Any failure
/// (cancellation, the budget, an unsafe entry, an I/O error) removes every file written, puts back
/// every file replaced and removes every folder created. A replaced file is moved aside into the
/// dataset's uploads folder (same volume: a rename) and deleted when the extraction completes.</item>
/// </list>
/// Indexing and builds are the caller's: this class only writes files.
/// </summary>
public sealed class DatasetArchiveExtractor
{
    private readonly IArchiveExtractor _archives;
    private readonly DatasetPathPolicy _paths;
    private readonly IScratchFileProvider _scratch;
    private readonly IFileSystem _fs;
    private readonly VolumeHeadroom _headroom;
    private readonly ILogger<DatasetArchiveExtractor> _logger;

    public DatasetArchiveExtractor(IArchiveExtractor archives, DatasetPathPolicy paths, IScratchFileProvider scratch,
        IFileSystem fs, VolumeHeadroom headroom, ILogger<DatasetArchiveExtractor> logger)
    {
        _archives = archives;
        _paths = paths;
        _scratch = scratch;
        _fs = fs;
        _headroom = headroom;
        _logger = logger;
    }

    /// <summary>Whether the file name has a supported archive format.</summary>
    public bool IsSupported(string fileName) => _archives.IsSupported(fileName);

    /// <summary>
    /// The size the archive has once extracted, when it is known without decompressing (random-access formats:
    /// zip, rar, 7z, tar; not compressed tarballs), else null.
    /// </summary>
    /// <exception cref="Exception">The archive cannot be opened.</exception>
    public long? FastUncompressedBytes(string archivePath)
    {
        using var session = _archives.Open(archivePath);
        return session.FastUncompressedBytes;
    }

    /// <summary>Extracts the archive of <paramref name="request"/> into its dataset; on failure the dataset is left as it was.</summary>
    /// <exception cref="Exceptions.InvalidDatasetPathException">An entry name is unsafe (zip-slip).</exception>
    /// <exception cref="Exceptions.QuotaExceededException">The cap or the disk head-room was reached.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<ArchiveExtractionResult> ExtractAsync(ArchiveExtractionRequest request,
        IProgress<ArchiveExtractionProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var journal = new Journal(this, request.DatasetFolder);
        var committed = false;
        try
        {
            var result = await ExtractEntriesAsync(request, journal, progress, ct);
            committed = true;
            return result;
        }
        finally
        {
            if (committed)
                journal.Commit();
            else
                journal.Rollback();
        }
    }

    private async Task<ArchiveExtractionResult> ExtractEntriesAsync(ArchiveExtractionRequest request, Journal journal,
        IProgress<ArchiveExtractionProgress>? progress, CancellationToken ct)
    {
        var budget = _headroom.CreateBudget(request.MaxBytes, request.DatasetFolder, request.SafetyMarginBytes);
        var extracted = new List<string>();
        var skipped = 0;
        var done = 0;

        // The session (and its file handle) is closed when the loop ends, before the caller deletes
        // or indexes anything
        using var session = _archives.Open(request.ArchivePath);
        var total = session.FastFileEntryCount ?? 0;
        progress?.Report(new ArchiveExtractionProgress(0, total, string.Empty, 0));

        foreach (var entry in session.Entries())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
                continue;

            var target = Target(request, entry.Key);
            if (target == null || !Allowed(request, target) || request.KeepExisting(target))
            {
                skipped++;
            }
            else
            {
                var local = DatasetPathPolicy.ToLocalPath(request.DatasetFolder, target);
                if (journal.BeforeWrite(local))
                    extracted.Add(target);

                // The cap and the head-room are checked before each chunk, so an entry whose header
                // under-reports its size cannot fill the volume
                await using (var source = entry.OpenStream())
                await using (var file = _fs.OpenWrite(local, FileMode.CreateNew))
                    await budget.CopyGuardedAsync(source, file, ct);
            }

            done++;
            progress?.Report(new ArchiveExtractionProgress(done, total, entry.Key, budget.BytesWritten));
        }

        return new ArchiveExtractionResult(extracted, skipped, budget.BytesWritten, total);
    }

    // The dataset path of an entry, or null to skip it (a folder entry, a reserved path)
    private string? Target(ArchiveExtractionRequest request, string key)
    {
        var name = _paths.NormalizeArchiveKey(key);
        var target = name.Path == null ? name : _paths.CheckIn(request.DestinationFolder, name.Path);
        switch (target.Rejected)
        {
            case null:
                return target.Path;
            case EntryPathRejection.Reserved:
                _logger.LogWarning("Skipping archive entry '{Key}': it is in a reserved folder", key);
                return null;
            case EntryPathRejection.Empty:
                return null;
            default:
                // The key passed the rule alone: joining a normalized folder cannot break it
                throw new Exceptions.InvalidDatasetPathException(key, target.Rejected, "archive entry");
        }
    }

    private bool Allowed(ArchiveExtractionRequest request, string target)
    {
        if (request.IsExtensionAllowed(target))
            return true;
        _logger.LogWarning("Skipping disallowed file type during extraction: '{Path}'", target);
        return false;
    }

    /// <summary>
    /// What one extraction changed in the dataset, so it can be undone: the files written (recorded
    /// before the first byte, so a partial file is removed too), the files replaced (moved aside)
    /// and the folders created. Never throws from <see cref="Commit"/> or <see cref="Rollback"/>:
    /// they run in a finally block.
    /// </summary>
    private sealed class Journal(DatasetArchiveExtractor owner, string datasetFolder)
    {
        private readonly List<string> _written = [];
        private readonly HashSet<string> _writtenSet = new(Common.PathComparison.Comparer);
        private readonly List<(string Target, ScratchFile Backup)> _replaced = [];
        private readonly List<string> _createdFolders = [];

        /// <summary>Prepares <paramref name="local"/> for a new file; false when this extraction already wrote it.</summary>
        public bool BeforeWrite(string local)
        {
            var fs = owner._fs;
            CreateFolders(Path.GetDirectoryName(local)!);

            if (_writtenSet.Contains(local))
            {
                // Two entries with the same normalized path: the last one wins, as in the archive tools
                fs.Delete(local);
                return false;
            }

            if (fs.Exists(local))
            {
                var backup = owner._scratch.CreateForDataset(datasetFolder, ".bak");
                fs.Move(local, backup.Path);
                _replaced.Add((local, backup));
            }

            _written.Add(local);
            _writtenSet.Add(local);
            return true;
        }

        public void Commit()
        {
            foreach (var (_, backup) in _replaced)
                backup.Dispose();
        }

        public void Rollback()
        {
            var fs = owner._fs;
            var logger = owner._logger;
            if (_written.Count > 0)
                logger.LogWarning("Rolling back an extraction: {Written} file(s) written, {Replaced} replaced",
                    _written.Count, _replaced.Count);

            foreach (var file in _written)
                fs.TryDelete(file, ex => logger.LogWarning(ex, "Could not delete the extracted file '{Path}'", file));

            foreach (var (target, backup) in _replaced)
            {
                try
                {
                    fs.Move(backup.Path, target);
                    backup.Release();
                }
                catch (Exception ex)
                {
                    // The original stays in the uploads folder: never delete it
                    backup.Release();
                    logger.LogError(ex, "Could not put back '{Path}'; the original is kept at '{Backup}'",
                        target, backup.Path);
                }
            }

            for (var i = _createdFolders.Count - 1; i >= 0; i--)
            {
                var folder = _createdFolders[i];
                try
                {
                    if (fs.FolderExists(folder) && !fs.EnumerateFileSystemEntries(folder).Any())
                        fs.FolderDelete(folder, false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not remove the folder '{Path}'", folder);
                }
            }
        }

        // Creates the missing folders of a file, top-down, and records them
        private void CreateFolders(string folder)
        {
            var fs = owner._fs;
            var missing = new Stack<string>();
            for (var f = folder; !string.IsNullOrEmpty(f) && !fs.FolderExists(f); f = Path.GetDirectoryName(f))
                missing.Push(f);

            while (missing.Count > 0)
            {
                var f = missing.Pop();
                fs.FolderCreate(f);
                _createdFolders.Add(f);
            }
        }
    }
}
