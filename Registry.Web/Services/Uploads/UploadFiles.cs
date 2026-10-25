#nullable enable
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

    /// <summary>Moves the temp file of <paramref name="part"/> to its dataset path (same-volume atomic move, no extra full-file copy); the local path.</summary>
    public string MoveIntoDataset(UploadRequestContext ctx, StreamedPart part)
    {
        var localFilePath = ctx.Ddb.GetLocalPath(part.Path);
        _fs.EnsureParentFolderExists(localFilePath);

        // Same-volume atomic move: no extra full-file copy (keeps the write time the hash hint carries)
        _fs.Move(part.File.TempPath, localFilePath, overwrite: true);
        return localFilePath;
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

    /// <summary>Deletes the temp file of a part when it is in this dataset's uploads folder (a path outside it is never ours to delete).</summary>
    public void DeleteLeftover(UploadRequestContext ctx, StreamedFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.TempPath) && IsInUploadsFolder(ctx.Ddb, file.TempPath))
            _fs.TryDelete(file.TempPath); // else the abandoned-upload sweep removes it
    }
}
