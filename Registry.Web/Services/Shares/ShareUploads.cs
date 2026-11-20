using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Shares;

/// <summary>The files of a share batch, in one request: added through the upload pipeline, recorded on the batch.</summary>
public sealed class ShareUploads
{
    private readonly ShareBatchStore _batches;
    private readonly IObjectsManager _objectsManager;
    private readonly IFileSystem _fileSystem;
    private readonly IDdbManager _ddbManager;
    private readonly DatasetPathPolicy _paths;
    private readonly ILogger<ShareUploads> _logger;

    public ShareUploads(ShareBatchStore batches, IObjectsManager objectsManager, IFileSystem fileSystem, IDdbManager ddbManager, DatasetPathPolicy paths, ILogger<ShareUploads> logger)
    {
        _batches = batches;
        _objectsManager = objectsManager;
        _fileSystem = fileSystem;
        _ddbManager = ddbManager;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the target organization/dataset of a running batch so the caller can stream the
    /// upload directly onto the dataset's storage volume.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <returns>The organization and dataset slugs of the running batch.</returns>
    public async Task<(string OrgSlug, string DsSlug)> GetUploadTarget(string token, CancellationToken ct = default)
    {
        var batch = await _batches.GetRunningAsync(token, ct);
        return (batch.Dataset.Organization.Slug, batch.Dataset.Slug);
    }

    /// <summary>
    /// Where a file of a share batch sent to <paramref name="path"/> is written, as the upload commit resolves it: the
    /// dataset path rule (<see cref="DatasetPathPolicy.Normalize"/>, a backslash is a '/'), then the letter case of the
    /// entry that holds the name (D-040, <see cref="DatasetEntryLookup.Locate"/>). <c>Replaced</c>: something held the
    /// name before the upload (a rollback of the batch keeps it, FR-XC-27).
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.InvalidDatasetPathException">Not a dataset path (400).</exception>
    internal static (string Path, bool Replaced) Target(DatasetPathPolicy paths, IDDB ddb, string path)
    {
        var location = DatasetEntryLookup.Locate(ddb, paths.Normalize(path));
        return (location.Path, location.Existing != null);
    }

    /// <summary>
    /// Finalizes a streamed share upload: validates the batch, moves the already-written temp file
    /// into place (via the objects manager) and records the batch entry.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <param name="path">The destination path within the dataset.</param>
    /// <param name="file">The temp file produced by <c>IObjectsManager.StreamToTempAsync</c>.</param>
    /// <returns>The upload result.</returns>
    public async Task<UploadResultDto> UploadStreamed(string token, string? path, StreamedFile file,
        CancellationToken ct = default)
    {
        var tempFilePath = file.TempPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new BadRequestException("Missing path");

            var batch = await _batches.GetRunningAsync(token, ct);

            var orgSlug = batch.Dataset.Organization.Slug;
            var dsSlug = batch.Dataset.Slug;

            // The path as the index stores it (D-040), and whether a file was there before the batch: it is replaced,
            // and a rollback keeps it (FR-XC-27)
            var target = Target(_paths, _ddbManager.Get(orgSlug, batch.Dataset.InternalRef), path);

            if (batch.Entries.Any(item => item.Path == target.Path))
                throw new BadRequestException("Entry already uploaded");

            _logger.LogInformation("Adding (streamed) '{Path}' in batch '{BatchToken}'", target.Path, batch.Token);

            // The commit reserves the quota on the authoritative size on disk
            var added = await _objectsManager.CommitStreamedAsync(orgSlug, dsSlug, target.Path, file);

            return await _batches.AddEntryAsync(batch, path, added, target.Replaced);
        }
        finally
        {
            // Backstop: drop the temp file if a pre-commit validation prevented the move
            _fileSystem.TryDelete(tempFilePath);
        }
    }
}
