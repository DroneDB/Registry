using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Entry = Registry.Web.Data.Models.Entry;

namespace Registry.Web.Services.Shares;

/// <summary>The files of a share batch, in one request: added through the upload pipeline, recorded on the batch.</summary>
public interface IShareUploads
{
    Task<(string OrgSlug, string DsSlug)> GetUploadTarget(string token, CancellationToken ct = default);
    Task<UploadResultDto> UploadStreamed(string token, string path, StreamedFile file, CancellationToken ct = default);
}

/// <summary>Default <see cref="IShareUploads"/> (RQ-12, out of <c>ShareManager</c>).</summary>
public sealed class ShareUploads : IShareUploads
{
    private readonly IShareBatchStore _batches;
    private readonly IObjectsManager _objectsManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ShareUploads> _logger;

    public ShareUploads(IShareBatchStore batches, IObjectsManager objectsManager, IFileSystem fileSystem, ILogger<ShareUploads> logger)
    {
        _batches = batches;
        _objectsManager = objectsManager;
        _fileSystem = fileSystem;
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
    /// Finalizes a streamed share upload: validates the batch, moves the already-written temp file
    /// into place (via the objects manager) and records the batch entry.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <param name="path">The destination path within the dataset.</param>
    /// <param name="file">The temp file produced by <c>IObjectsManager.StreamToTempAsync</c>.</param>
    /// <returns>The upload result.</returns>
    public async Task<UploadResultDto> UploadStreamed(string token, string path, StreamedFile file,
        CancellationToken ct = default)
    {
        var tempFilePath = file.TempPath;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new BadRequestException("Missing path");

            var batch = await _batches.GetRunningAsync(token, ct);

            if (batch.Entries.Any(item => item.Path == path))
                throw new BadRequestException("Entry already uploaded");

            _logger.LogInformation("Adding (streamed) '{Path}' in batch '{BatchToken}'", path, batch.Token);

            var orgSlug = batch.Dataset.Organization.Slug;
            var dsSlug = batch.Dataset.Slug;

            // The commit reserves the quota on the authoritative size on disk
            var added = await _objectsManager.CommitStreamedAsync(orgSlug, dsSlug, path, file);

            return await _batches.AddEntryAsync(batch, path, added);
        }
        finally
        {
            // Backstop: drop the temp file if a pre-commit validation prevented the move
            _fileSystem.TryDelete(tempFilePath);
        }
    }
}
