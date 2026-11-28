using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Uploads;

/// <summary>
/// The files of an upload request (out of <c>UploadManager</c>): the path rule, streaming a body to a temp
/// file in the dataset, and what the commit does with them on disk (validate, size, move into place, quarantine,
/// delete what was never moved).
/// </summary>
public sealed class UploadFiles
{
    private readonly DatasetPathPolicy _paths;
    private readonly ImportSettings _import;
    private readonly ScratchFileProvider _scratch;
    private readonly IFileSystem _fs;
    private readonly ILogger<UploadFiles> _logger;

    public UploadFiles(DatasetPathPolicy paths, IOptions<ImportSettings> importSettings, ScratchFileProvider scratch,
        IFileSystem fs, ILogger<UploadFiles> logger)
    {
        _paths = paths;
        _import = importSettings.Value;
        _scratch = scratch;
        _fs = fs;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="tempFilePath"/> is under the reserved uploads folder of the dataset of <paramref name="ddb"/>.</summary>
    public static bool IsInUploadsFolder(IDDB ddb, string tempFilePath) =>
        CommonUtils.IsUnder(Path.Combine(ddb.DatasetFolderPath, IDDB.UploadsFolderName), tempFilePath);

    /// <summary>The normalized dataset path a file uploaded to <paramref name="path"/> is stored at.</summary>
    /// <exception cref="InvalidDatasetPathException">Not a dataset path (NUL, absolute, "..", reserved).</exception>
    /// <exception cref="ExtensionBlockedException">The import policy blocks the extension.</exception>
    public string ValidatePath(string path)
    {
        // libddb's path rule (NUL, absolute, "..", reserved), then the extension policy: refused
        // before anything is written into the dataset
        var normalized = _paths.Normalize(path);
        if (!_import.IsExtensionAllowed(normalized))
            throw new ExtensionBlockedException(normalized, _import.AllowedFileExtensions.Length > 0);
        return normalized;
    }

    /// <summary>
    /// The dataset path a file uploaded to <paramref name="path"/> is written at (D-040): the path rule of
    /// <see cref="ValidatePath"/>, then the name rule of <see cref="DatasetEntryLookup.ResolveOutput"/>, so a name that
    /// exists (without regard to letter case) is refused unless <paramref name="overwrite"/>, and replaced in the
    /// letter case of the existing entry when it is.
    /// </summary>
    /// <exception cref="InvalidDatasetPathException">Not a dataset path.</exception>
    /// <exception cref="ExtensionBlockedException">The import policy blocks the extension.</exception>
    /// <exception cref="NameClashException">The name exists and <paramref name="overwrite"/> is false, or it is a folder.</exception>
    public string ResolveTarget(UploadRequestContext ctx, string path, bool overwrite) =>
        DatasetEntryLookup.ResolveOutput(ctx.Ddb, ValidatePath(path), overwrite);

    /// <summary>Streams one file part to a temp file in the dataset, hashing it on the way.</summary>
    public async Task<StreamedFile> StreamAsync(UploadRequestContext ctx, Stream body, CancellationToken ct = default)
    {
        var file = await DatasetUploads.StreamToTempAsync(_scratch, _fs, ctx.Ddb.DatasetFolderPath, body, ct);
        ctx.TrackTempFile(file.TempPath);
        return file;
    }

    /// <summary>Deletes the temp file of a part that will not be committed.</summary>
    public void Discard(StreamedFile file) =>
        _fs.TryDelete(file.TempPath, ex => _logger.LogDebug(ex, "Cannot delete the temp upload '{TempFile}'", file.TempPath));

    /// <summary>
    /// Defense-in-depth: the temp file must live inside this dataset's reserved uploads folder, so an
    /// arbitrary/foreign path is never moved nor deleted.
    /// </summary>
    public void ValidateTempFile(UploadRequestContext ctx, string tempFilePath)
    {
        if (string.IsNullOrWhiteSpace(tempFilePath))
            throw new InvalidOperationException("Missing temporary upload path");

        if (!IsInUploadsFolder(ctx.Ddb, tempFilePath))
            throw new InvalidOperationException("Invalid temporary upload path");
    }

    /// <summary>The authoritative size of the temp file, from disk: never trust the caller-provided count for the quota check.</summary>
    public long ActualSize(StreamedFile file)
    {
        var actual = _fs.GetFileSize(file.TempPath);
        if (actual != file.Bytes)
            _logger.LogWarning("Streamed upload size mismatch: {Reported} bytes reported, temp file is {Actual} bytes",
                file.Bytes, actual);
        return actual;
    }

    /// <summary>
    /// Moves the temp file of <paramref name="part"/> to its dataset path (same-volume atomic move, no extra full-file
    /// copy); the local path. A part without <see cref="StreamedPart.Overwrite"/> never replaces a file, also one
    /// written since its name was checked.
    /// </summary>
    /// <exception cref="NameClashException">The part does not overwrite and a file holds its path.</exception>
    public MovedUploadPart MoveIntoDataset(UploadRequestContext ctx, StreamedPart part)
    {
        var localFilePath = ctx.Ddb.GetLocalPath(part.Path);
        _fs.EnsureParentFolderExists(localFilePath);

        var replaced = _fs.Exists(localFilePath);
        if (!part.Overwrite && replaced)
            throw new NameClashException(part.Path);

        try
        {
            // Same-volume atomic move: no extra full-file copy (keeps the write time the hash hint carries)
            _fs.Move(part.File.TempPath, localFilePath, overwrite: part.Overwrite);
        }
        catch (IOException) when (!part.Overwrite && _fs.Exists(localFilePath))
        {
            // Another writer took the name between the check and the move
            throw new NameClashException(part.Path);
        }

        return new MovedUploadPart(part, localFilePath, replaced);
    }

    /// <summary>
    /// What becomes of a file placed in the dataset that has no entry. An error that passes (the index is busy, its
    /// write did not finish in time) takes the file back out (<see cref="Withdraw"/>), so the retry meets a free name and
    /// the file is not left unindexed, which would be refused as a clash and counted as a replaced file by a share
    /// batch. Anything else is quarantined. A file whose index write may still commit (<see cref="OutcomeUnknown"/>)
    /// stays: taken out, the index would list it without its file once that write commits. Never throws.
    /// </summary>
    public void DropUnindexed(UploadRequestContext ctx, MovedUploadPart moved, Exception error)
    {
        if (OutcomeUnknown(error))
            _logger.LogWarning(error, "'{Path}' in dataset '{OrgSlug}/{DsSlug}' may still be indexed: it stays",
                moved.Part.Path, ctx.OrgSlug, ctx.DsSlug);
        else if (error is TransientException or DdbBusyException)
            Withdraw(ctx, moved, error);
        else
            Quarantine(ctx, moved.Part.Path, moved.LocalPath, error);
    }

    /// <summary>
    /// Whether the index write of a file may still commit: a batch is writing it, or the wait for it was canceled
    /// without the queue saying what became of it.
    /// </summary>
    public static bool OutcomeUnknown(Exception? error) =>
        error is IndexWritePendingException or OperationCanceledException;

    /// <summary>
    /// The index write of a file left in place because it was still running when its request stopped waiting has
    /// failed after all: the file is taken out as <see cref="DropUnindexed"/> takes it out at once, unless the dataset
    /// has made it its own meanwhile: another upload put its file at that path (the file there is not this upload's),
    /// or a new file was indexed under the name. Never throws.
    /// </summary>
    /// <returns>Whether the file of this upload stays in the dataset as an entry (indexed meanwhile).</returns>
    public bool DropUnindexedLate(UploadRequestContext ctx, MovedUploadPart moved, Exception error)
    {
        // What holds the path when that cannot be told: the file stays, for the reconciliation sweep
        var holder = BestEffort.Get(() => HolderOf(ctx, moved), ex =>
        {
            _logger.LogError(ex, "Cannot tell whether '{Path}' in dataset '{OrgSlug}/{DsSlug}' is still the upload's: " +
                                 "it stays", moved.Part.Path, ctx.OrgSlug, ctx.DsSlug);
            return PathHolder.Index;
        });

        switch (holder)
        {
            case PathHolder.OtherUpload:
                _logger.LogInformation(error, "'{Path}' in dataset '{OrgSlug}/{DsSlug}' was not indexed, and the file " +
                                              "at its path is no longer the upload's: it is left as it is",
                    moved.Part.Path, ctx.OrgSlug, ctx.DsSlug);
                return false;
            case PathHolder.Index:
                _logger.LogInformation(error, "'{Path}' in dataset '{OrgSlug}/{DsSlug}' was indexed by another write " +
                                              "meanwhile: it stays", moved.Part.Path, ctx.OrgSlug, ctx.DsSlug);
                return true;
            default:
                DropUnindexed(ctx, moved, error);
                return false;
        }
    }

    private enum PathHolder
    {
        // The file this upload moved there, which nothing indexed
        Upload,

        // Not that file any more: another upload wrote the path since, or the file is gone
        OtherUpload,

        // The file this upload moved there, indexed by another write meanwhile
        Index
    }

    private PathHolder HolderOf(UploadRequestContext ctx, MovedUploadPart moved)
    {
        if (!IsPlacedFile(moved))
            return PathHolder.OtherUpload;
        return !moved.ReplacedFile && ctx.Ddb.EntryExists(moved.Part.Path) ? PathHolder.Index : PathHolder.Upload;
    }

    // Whether the file at the dataset path is still the one this upload moved there: the move keeps the size and the
    // write time it had after it was written (its hash hint), a file written there since has others
    private bool IsPlacedFile(MovedUploadPart moved) =>
        _fs.Exists(moved.LocalPath) &&
        _fs.GetFileSize(moved.LocalPath) == moved.Part.File.Hash.Size &&
        _fs.GetLastWriteTime(moved.LocalPath).ToUniversalTime() == moved.Part.File.Hash.ModifiedUtc;

    /// <summary>
    /// Takes back a file that was placed in the dataset and could not be indexed for a reason that passes (the index is
    /// busy, or its write did not finish in time): the file goes back to the temporary path it was streamed to, which
    /// the commit deletes (or leaves to its <see cref="StreamedFile.Source"/>), so the name is free again and the same
    /// upload sent once more is not refused as a clash with a file the dataset does not list. A file that replaced
    /// another one stays: the old content is gone, and the index still lists its name. Never throws: a file that cannot
    /// be moved back is quarantined.
    /// </summary>
    public void Withdraw(UploadRequestContext ctx, MovedUploadPart moved, Exception cause)
    {
        if (moved.ReplacedFile)
        {
            _logger.LogWarning(cause, "'{Path}' in dataset '{OrgSlug}/{DsSlug}' replaced a file and was not indexed: it stays",
                moved.Part.Path, ctx.OrgSlug, ctx.DsSlug);
            return;
        }

        BestEffort.Run(() =>
        {
            if (!_fs.Exists(moved.LocalPath))
                return;

            _fs.Move(moved.LocalPath, moved.Part.File.TempPath, overwrite: true);
            _logger.LogWarning(cause, "'{Path}' in dataset '{OrgSlug}/{DsSlug}' was not indexed in time: the upload is withdrawn",
                moved.Part.Path, ctx.OrgSlug, ctx.DsSlug);
        }, ex =>
        {
            _logger.LogError(ex, "Cannot withdraw '{Path}' in dataset '{OrgSlug}/{DsSlug}'", moved.Part.Path,
                ctx.OrgSlug, ctx.DsSlug);
            Quarantine(ctx, moved.Part.Path, moved.LocalPath, cause);
        });
    }

    /// <summary>
    /// Compensating action for the commit flow: moves a file that was placed on disk but failed to index into a
    /// per-dataset quarantine folder, so it is neither lost nor left as an untracked orphan. Never throws.
    /// </summary>
    public void Quarantine(UploadRequestContext ctx, string path, string localFilePath, Exception cause)
    {
        try
        {
            if (!_fs.Exists(localFilePath))
                return;

            var quarantineDir = Path.Combine(ctx.Ddb.DatasetFolderPath, IDDB.UploadsFolderName, IDDB.QuarantineFolderName);
            _fs.FolderCreate(quarantineDir);

            // GUID suffix for collision resistance: parallel failures for paths with the same
            // basename (e.g. a/data.tif, b/data.tif) could otherwise generate the same target
            // and overwrite silently.
            var quarantinePath = Path.Combine(quarantineDir,
                $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");

            _fs.Move(localFilePath, quarantinePath, overwrite: true);

            _logger.LogError(cause,
                "Quarantined '{Path}' in dataset '{OrgSlug}/{DsSlug}' after index failure -> '{QuarantinePath}'",
                path, ctx.OrgSlug, ctx.DsSlug, quarantinePath);
        }
        catch (Exception quarantineEx)
        {
            _logger.LogError(quarantineEx,
                "Failed to quarantine '{Path}' in dataset '{OrgSlug}/{DsSlug}' after index failure (original cause: {Cause})",
                path, ctx.OrgSlug, ctx.DsSlug, cause.Message);
        }
    }

    /// <summary>
    /// The commit is done with the temp file of a part: deleted when it is in this dataset's uploads folder (a path
    /// outside it is never ours to delete), or handed back to its <see cref="StreamedFile.Source"/>, which keeps what is
    /// still there. Never throws.
    /// </summary>
    public void DeleteLeftover(UploadRequestContext ctx, StreamedFile file)
    {
        if (!HandBack(file, _logger) && !string.IsNullOrWhiteSpace(file.TempPath) &&
            IsInUploadsFolder(ctx.Ddb, file.TempPath))
            _fs.TryDelete(file.TempPath); // else the abandoned-upload sweep removes it
    }

    /// <summary>
    /// Hands a temp file that outlives the request back to its <see cref="StreamedFile.Source"/>; false when it has
    /// none (the temp file is the commit's to delete). Never throws.
    /// </summary>
    public static bool HandBack(StreamedFile file, ILogger logger)
    {
        if (file.Source is not { } source)
            return false;

        BestEffort.Run(source.Release,
            ex => logger.LogError(ex, "Cannot hand back the temp upload '{TempFile}'", file.TempPath));
        return true;
    }
}
