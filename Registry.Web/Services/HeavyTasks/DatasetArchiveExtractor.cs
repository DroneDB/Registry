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
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>What to extract, where, and what to skip.</summary>
/// <param name="ArchivePath">The archive, a local file.</param>
/// <param name="DatasetFolder">The dataset folder (local).</param>
/// <param name="DestinationFolder">The normalized dataset path the entries go under; "" for the dataset root.</param>
/// <param name="IsExtensionAllowed">The import policy: an entry whose extension it refuses is skipped.</param>
/// <param name="KeepExisting">True when the file at this dataset path must be kept (the entry is skipped).</param>
/// <param name="MaxBytes">The cap on the bytes the extraction writes (it counts only the entries that it writes, never a
/// skipped one); 0 (or less) disables it. The caller passes the smaller of the archive size limit and the bytes the owner of
/// the organization may still store (FR-XC-26).</param>
/// <param name="SafetyMarginBytes">The disk head-room to keep (<see cref="VolumeHeadroom.MarginBytes"/>).</param>
/// <param name="Writes">The write record of the task that extracts (FR-XC-27): every file and folder the extraction
/// creates is recorded before it is created; a file it replaces (<paramref name="KeepExisting"/> false for an existing
/// file) is not. Null: nothing is recorded.</param>
/// <param name="Exclude">The entries that are not extracted whatever their type (the internal folders of a photogrammetry
/// result, FR-PRC-41): the dataset path it accepts is skipped with the reason <see cref="ArchiveSkipReason.Excluded"/>,
/// apart from <paramref name="IsExtensionAllowed"/>. Null: nothing is excluded.</param>
/// <param name="MaxBytesMessage">The text of the failure when <paramref name="MaxBytes"/> is reached, for a cap that is the
/// storage quota of the owner (the 507 text, FR-XC-26); null: the text of the archive size limit.</param>
public sealed record ArchiveExtractionRequest(
    string ArchivePath,
    string DatasetFolder,
    string DestinationFolder,
    Func<string, bool> IsExtensionAllowed,
    Func<string, bool> KeepExisting,
    long MaxBytes,
    long SafetyMarginBytes,
    Registry.Ports.Import.IImportWriteRecord? Writes = null,
    Func<string, bool>? Exclude = null,
    string? MaxBytesMessage = null);

/// <summary>Why an entry of an archive was not extracted (FR-PRC-30).</summary>
public enum ArchiveSkipReason
{
    /// <summary>The import policy refuses its extension.</summary>
    FileTypeNotAllowed,

    /// <summary>A file is at its dataset path and is kept (no overwrite).</summary>
    KeptExisting,

    /// <summary>It is in a reserved folder of the dataset (".ddb", the uploads folder).</summary>
    ReservedPath,

    /// <summary>The caller excludes it (<see cref="ArchiveExtractionRequest.Exclude"/>, FR-PRC-41).</summary>
    Excluded
}

/// <summary>An entry of an archive that was not extracted.</summary>
/// <param name="Path">The dataset path of the entry (its name in the archive when it has no valid dataset path).</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record ArchiveSkippedEntry(string Path, ArchiveSkipReason Reason)
{
    /// <summary>The reason as the logs and the toasts of the Hub write it.</summary>
    public string ReasonText => Reason switch
    {
        ArchiveSkipReason.FileTypeNotAllowed => "file type not allowed",
        ArchiveSkipReason.KeptExisting => "file already exists",
        ArchiveSkipReason.ReservedPath => "reserved path",
        _ => "excluded"
    };

    /// <summary>The line of the task log for this entry: <c>Skipped: vectors/tool.exe (file type not allowed)</c>.</summary>
    public string LogLine => $"Skipped: {Path} ({ReasonText})";
}

/// <summary>Progress of an extraction, after each entry (and once before the first, with <see cref="Done"/> 0).</summary>
/// <param name="Done">Entries handled (extracted or skipped).</param>
/// <param name="Total">File entries in the archive; 0 when unknown without decompressing (compressed tarballs).</param>
/// <param name="Key">The name of the entry just handled ("" before the first).</param>
/// <param name="BytesWritten">Bytes extracted so far.</param>
public sealed record ArchiveExtractionProgress(int Done, int Total, string Key, long BytesWritten);

/// <summary>What an extraction wrote.</summary>
/// <param name="Extracted">The dataset paths written, in archive order, each once.</param>
/// <param name="Skipped">Entries skipped, with the reason of each: kept existing files, refused extensions, reserved paths,
/// excluded entries (a folder entry, which holds no file, is not one).</param>
/// <param name="BytesWritten">Bytes extracted.</param>
/// <param name="Total">File entries in the archive (0 when it was unknown).</param>
public sealed record ArchiveExtractionResult(IReadOnlyList<string> Extracted, IReadOnlyList<ArchiveSkippedEntry> Skipped,
    long BytesWritten, int Total)
{
    /// <summary>How many skipped entries the task log names one by one; the others are counted.</summary>
    public const int MaxLoggedSkipped = 50;

    /// <summary>
    /// The lines of the task log about what was not extracted (FR-PRC-30, FR-PRC-41), then the summary line the Hub reads:
    /// <c>Skipped: vectors/tool.exe (file type not allowed)</c> for each of the first <see cref="MaxLoggedSkipped"/>
    /// skipped entries that are not excluded, the number of those that are excluded (internal files, never named one by
    /// one), and <c>Extraction complete: 8 extracted, 1 skipped</c>. A skipped entry never reads as extracted.
    /// </summary>
    public IReadOnlyList<string> LogLines()
    {
        var lines = new List<string>();
        var named = Skipped.Where(s => s.Reason != ArchiveSkipReason.Excluded).ToList();
        lines.AddRange(named.Take(MaxLoggedSkipped).Select(s => s.LogLine));
        if (named.Count > MaxLoggedSkipped)
            lines.Add($"Skipped: and {named.Count - MaxLoggedSkipped} more");

        var excluded = Skipped.Count - named.Count;
        if (excluded > 0)
            lines.Add($"Excluded {excluded} internal file(s) of the archive");

        lines.Add($"Extraction complete: {Extracted.Count} extracted, {Skipped.Count} skipped");
        return lines;
    }
}

/// <summary>
/// The one archive extraction into a dataset, shared by the archive extract tool, the
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
    private readonly ScratchFileProvider _scratch;
    private readonly IFileSystem _fs;
    private readonly VolumeHeadroom _headroom;
    private readonly ILogger<DatasetArchiveExtractor> _logger;

    public DatasetArchiveExtractor(IArchiveExtractor archives, DatasetPathPolicy paths, ScratchFileProvider scratch,
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

    /// <summary>
    /// The size that <paramref name="existingBytes"/> gives (the storage use of the owner counts it now) for the files an
    /// extraction of <paramref name="request"/> replaces: the dataset paths of its file entries that it would write (not
    /// excluded, of an allowed type, not kept), each path once. The storage quota counts only the growth of a replace
    /// (FR-XC-26), as for a copy. Reads the list of entries without writing anything (a compressed tarball is read
    /// through); an entry without a dataset path is not counted (the extraction skips or refuses it).
    /// </summary>
    /// <exception cref="Exception">The archive cannot be opened or read.</exception>
    public long ReplacedBytes(ArchiveExtractionRequest request, Func<string, long> existingBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(existingBytes);

        using var session = _archives.Open(request.ArchivePath);
        var seen = new HashSet<string>(Common.PathComparison.Comparer);
        long total = 0;
        foreach (var entry in session.Entries())
        {
            if (entry.IsDirectory)
                continue;

            var name = _paths.NormalizeArchiveKey(entry.Key);
            var target = name.Path == null ? name : _paths.CheckIn(request.DestinationFolder, name.Path);
            if (target.Rejected != null || target.Path is not { } path || !seen.Add(path))
                continue;

            if (request.Exclude?.Invoke(path) == true || !request.IsExtensionAllowed(path) || request.KeepExisting(path))
                continue;

            total += Math.Max(0, existingBytes(path));
        }

        return total;
    }

    /// <summary>Extracts the archive of <paramref name="request"/> into its dataset; on failure the dataset is left as it was.</summary>
    /// <exception cref="Exceptions.InvalidDatasetPathException">An entry name is unsafe (zip-slip).</exception>
    /// <exception cref="Exceptions.QuotaExceededException">The cap or the disk head-room was reached.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was canceled.</exception>
    public async Task<ArchiveExtractionResult> ExtractAsync(ArchiveExtractionRequest request,
        IProgress<ArchiveExtractionProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var journal = new Journal(this, request.DatasetFolder, request.Writes);
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

    /// <summary>How many entries are extracted, recorded and moved into the dataset together.</summary>
    internal int PlaceBatchSize { get; init; } = TaskImportWrites.SaveBatch;

    // An entry extracted to its temporary file, to be moved to its dataset path
    private sealed record StagedEntry(string Key, string Target, string Local, ScratchFile Temp);

    // Every entry is extracted to a temporary file of the dataset's uploads folder (never part of the dataset), a batch
    // at a time; then the batch is recorded in the task's write record and saved with one change (FR-XC-27, §8.1), and
    // only then its folders are created and its files moved into place: a process lost at any point leaves no file in
    // the dataset that the record does not hold
    private async Task<ArchiveExtractionResult> ExtractEntriesAsync(ArchiveExtractionRequest request, Journal journal,
        IProgress<ArchiveExtractionProgress>? progress, CancellationToken ct)
    {
        var budget = _headroom.CreateBudget(request.MaxBytes, request.DatasetFolder, request.SafetyMarginBytes,
            request.MaxBytesMessage);
        var extracted = new List<string>();
        var skipped = new List<ArchiveSkippedEntry>();
        var done = 0;
        var batch = new List<StagedEntry>();

        try
        {
            // The session (and its file handle) is closed when the loop ends, before the caller deletes
            // or indexes anything
            using (var session = _archives.Open(request.ArchivePath))
            {
                var total = session.FastFileEntryCount ?? 0;
                progress?.Report(new ArchiveExtractionProgress(0, total, string.Empty, 0));

                foreach (var entry in session.Entries())
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                        continue;

                    var skippedBefore = skipped.Count;
                    var target = Target(request, entry.Key, skipped);
                    if (target != null && Skip(request, target) is { } reason)
                    {
                        skipped.Add(new ArchiveSkippedEntry(target, reason));
                    }
                    else if (target != null)
                    {
                        var temp = _scratch.CreateForDataset(request.DatasetFolder);
                        batch.Add(new StagedEntry(entry.Key, target,
                            DatasetPathPolicy.ToLocalPath(request.DatasetFolder, target), temp));

                        // The cap and the head-room are checked before each chunk, so an entry whose header
                        // under-reports its size cannot fill the volume
                        await using (var source = entry.OpenStream())
                        await using (var file = _fs.OpenWrite(temp.Path, FileMode.CreateNew))
                            await budget.CopyGuardedAsync(source, file, ct);

                        if (batch.Count >= Math.Max(1, PlaceBatchSize))
                        {
                            await PlaceAsync(request, journal, batch, extracted, skipped, ct);
                            batch.Clear();
                        }
                    }

                    done++;
                    // The message of an entry is shown only for a file that is written: a skipped entry is not named as if
                    // it were extracted (FR-PRC-30)
                    var written = skipped.Count == skippedBefore;
                    progress?.Report(new ArchiveExtractionProgress(done, total, written ? entry.Key : string.Empty,
                        budget.BytesWritten));
                }

                await PlaceAsync(request, journal, batch, extracted, skipped, ct);
                batch.Clear();
                return new ArchiveExtractionResult(extracted, skipped, budget.BytesWritten, total);
            }
        }
        finally
        {
            // The entries not moved into the dataset (a failure, a cancel)
            foreach (var staged in batch)
                staged.Temp.Dispose();
        }
    }

    // Records the batch (the new files, stamped from their complete temporary files, and the folders they need), saves
    // the record, then moves each file to its place. The entries skipped because their file appeared meanwhile
    private async Task PlaceAsync(ArchiveExtractionRequest request, Journal journal, List<StagedEntry> batch,
        List<string> extracted, List<ArchiveSkippedEntry> skipped, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        var replacing = new HashSet<string>(Common.PathComparison.Comparer);
        foreach (var staged in batch)
            if (!journal.Plan(staged.Local, staged.Target, staged.Temp.Path))
                replacing.Add(staged.Local);

        if (request.Writes != null)
            await TaskImportWrites.SaveAsync(request.Writes, ct);

        foreach (var staged in batch)
        {
            var placed = journal.Place(staged.Local, staged.Temp, replacing.Contains(staged.Local));
            if (placed is null)
            {
                // Another writer created the file between the check and the move: it is kept, never replaced, and it
                // is not the extraction's (its record drops it)
                _logger.LogWarning("Skipping archive entry '{Key}': '{Path}' was created meanwhile", staged.Key,
                    staged.Target);
                if (request.Writes != null)
                    await request.Writes.ForgetAsync(staged.Target, CancellationToken.None);
                skipped.Add(new ArchiveSkippedEntry(staged.Target, ArchiveSkipReason.KeptExisting));
                continue;
            }

            if (placed.Value)
                extracted.Add(staged.Target);
        }
    }

    // The dataset path of an entry, or null when it has none: a folder entry without a name, which holds no file, and a
    // reserved path, which is skipped and listed (FR-PRC-30)
    private string? Target(ArchiveExtractionRequest request, string key, List<ArchiveSkippedEntry> skipped)
    {
        var name = _paths.NormalizeArchiveKey(key);
        var target = name.Path == null ? name : _paths.CheckIn(request.DestinationFolder, name.Path);
        switch (target.Rejected)
        {
            case null:
                return target.Path;
            case EntryPathRejection.Reserved:
                _logger.LogWarning("Skipping archive entry '{Key}': it is in a reserved folder", key);
                skipped.Add(new ArchiveSkippedEntry(key, ArchiveSkipReason.ReservedPath));
                return null;
            case EntryPathRejection.Empty:
                return null;
            default:
                // The key passed the rule alone: joining a normalized folder cannot break it
                throw new Exceptions.InvalidDatasetPathException(key, target.Rejected, "archive entry");
        }
    }

    // Why the entry at the dataset path is not extracted, or null when it is: excluded by the caller, refused by the
    // import policy (a file type), or a file kept
    private ArchiveSkipReason? Skip(ArchiveExtractionRequest request, string target)
    {
        if (request.Exclude?.Invoke(target) == true)
            return ArchiveSkipReason.Excluded;

        if (!request.IsExtensionAllowed(target))
        {
            _logger.LogWarning("Skipping disallowed file type during extraction: '{Path}'", target);
            return ArchiveSkipReason.FileTypeNotAllowed;
        }

        return request.KeepExisting(target) ? ArchiveSkipReason.KeptExisting : null;
    }

    /// <summary>
    /// What one extraction changed in the dataset, so it can be undone: the files moved into it, the files replaced
    /// (moved aside) and the folders created. Never throws from <see cref="Commit"/> or <see cref="Rollback"/>:
    /// they run in a finally block.
    /// </summary>
    private sealed class Journal(DatasetArchiveExtractor owner, string datasetFolder,
        Registry.Ports.Import.IImportWriteRecord? writes)
    {
        private readonly List<string> _written = [];
        private readonly HashSet<string> _writtenSet = new(Common.PathComparison.Comparer);

        // The files that were there before the extraction and that it replaces: never recorded, also when a later
        // entry has the same path again (FR-XC-27)
        private readonly HashSet<string> _replacedSet = new(Common.PathComparison.Comparer);
        private readonly List<(string Target, ScratchFile Backup)> _replaced = [];
        private readonly List<string> _createdFolders = [];
        private readonly HashSet<string> _recordedFolders = new(Common.PathComparison.Comparer);

        /// <summary>
        /// Records what placing the file at <paramref name="local"/> (the dataset path <paramref name="target"/>) will
        /// create, before anything of it exists: the missing folders, and the file when nothing is there (stamped from
        /// <paramref name="tempFile"/>, its complete content). False when it replaces a file that is there, which is not
        /// recorded (FR-XC-27): the rollback must not delete it.
        /// </summary>
        public bool Plan(string local, string target, string tempFile)
        {
            var fs = owner._fs;
            var missing = new Stack<string>();
            for (var f = Path.GetDirectoryName(local); !string.IsNullOrEmpty(f) && !fs.FolderExists(f);
                 f = Path.GetDirectoryName(f))
                missing.Push(f);
            while (missing.Count > 0)
            {
                var f = missing.Pop();
                if (_recordedFolders.Add(f))
                    writes?.RecordFolder(Path.GetRelativePath(datasetFolder, f).Replace('\\', '/'));
            }

            // Written by this extraction already (two entries with the same normalized path), or not there: its own.
            // A file that was there before (replaced by an earlier entry) stays out of the record
            if (_replacedSet.Contains(local) || (!_writtenSet.Contains(local) && fs.Exists(local)))
            {
                _replacedSet.Add(local);
                return false;
            }

            writes?.Record(target);
            writes?.Written(target, tempFile);
            return true;
        }

        /// <summary>
        /// Moves <paramref name="temp"/> to <paramref name="local"/>, creating its folders: true when the dataset path
        /// is new to this extraction, false when the extraction wrote it already (the last entry wins), null when the
        /// file appeared meanwhile (another writer created it after <see cref="Plan"/>: kept, never replaced).
        /// </summary>
        public bool? Place(string local, ScratchFile temp, bool replacing)
        {
            var fs = owner._fs;
            CreateFolders(Path.GetDirectoryName(local)!);

            if (_writtenSet.Contains(local))
            {
                // Two entries with the same normalized path: the last one wins, as in the archive tools
                fs.Delete(local);
                fs.Move(temp.Path, local, false);
                temp.Release();
                return false;
            }

            if (replacing && fs.Exists(local))
            {
                var backup = owner._scratch.CreateForDataset(datasetFolder, ".bak");
                fs.Move(local, backup.Path);
                _replaced.Add((local, backup));
            }

            try
            {
                fs.Move(temp.Path, local, false);
            }
            catch (IOException) when (!replacing && fs.Exists(local))
            {
                return null;
            }

            temp.Release();
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

        // Creates the missing folders of a file, top-down (recorded and saved by Plan)
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
