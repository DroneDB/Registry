using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Shares;

/// <summary>The large files of a share batch, in chunks: written at their offsets next to the dataset, taken by a rename when complete.</summary>
public sealed class ShareChunkedUploads
{
    private readonly ShareBatchStore _batches;
    private readonly IStorageQuota _quota;
    private readonly ChunkStore _chunks;
    private readonly ScratchFileProvider _scratch;
    private readonly IDdbManager _ddbManager;
    private readonly IObjectsManager _objectsManager;
    private readonly DatasetPathPolicy _paths;
    private readonly IAuthManager _authManager;
    private readonly ILogger<ShareChunkedUploads> _logger;

    public ShareChunkedUploads(ShareBatchStore batches, IStorageQuota quota, ChunkStore chunks, ScratchFileProvider scratch, IDdbManager ddbManager, IObjectsManager objectsManager, DatasetPathPolicy paths, IAuthManager authManager, ILogger<ShareChunkedUploads> logger)
    {
        _batches = batches;
        _quota = quota;
        _chunks = chunks;
        _scratch = scratch;
        _ddbManager = ddbManager;
        _objectsManager = objectsManager;
        _paths = paths;
        _authManager = authManager;
        _logger = logger;
    }

    public async Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream,
        CancellationToken ct = default)
    {
        // Validate input parameters
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("Missing token");

        if (chunkInfo == null)
            throw new BadRequestException("Missing chunk information");

        if (chunkStream == null || !chunkStream.CanRead)
            throw new BadRequestException("Invalid chunk data stream");

        if (string.IsNullOrWhiteSpace(chunkInfo.FileId))
            throw new BadRequestException("Missing file ID");

        if (string.IsNullOrWhiteSpace(chunkInfo.Path))
            throw new BadRequestException("Missing path");

        // Verify the batch is valid and running
        var batch = await _batches.GetRunningAsync(token, ct);

        // Check if path is allowed for this batch (the batch records the paths normalized, D-040)
        if (!await _batches.IsPathAllowedAsync(token, _paths.Normalize(chunkInfo.Path), ct))
            throw new BadRequestException($"Path '{chunkInfo.Path}' is not allowed in this batch");

        var root = ChunksRoot(batch);
        var userId = await _authManager.GetCurrentUserId();
        var key = ChunkedUploadManager.SessionKey(userId, chunkInfo.FileId);
        var existing = _chunks.GetStatus(root, key);
        if (existing == null)
        {
            // A new upload: check the owner of the organization has room for the whole file before its first byte (FR-XC-26)
            await _quota.CheckOwnerStorageAsync(batch.Dataset.Organization.OwnerId, chunkInfo.TotalFileSize, ct);
        }
        else
            ChunkedUploadManager.EnsureOwner(userId, existing.Manifest, chunkInfo.FileId);

        _logger.LogDebug("Processing chunk {ChunkIndex}/{TotalChunks} for file {FileName} (FileId: {FileId})",
            chunkInfo.ChunkIndex, chunkInfo.TotalChunks, chunkInfo.FileName, chunkInfo.FileId);

        // Every chunk but the last has the same size; a single chunk is the whole file
        var chunkSize = chunkInfo.ChunkSize ?? (chunkInfo.TotalChunks == 1
            ? chunkInfo.TotalFileSize
            : throw new BadRequestException("ChunkSize is required when a file is sent in more than one chunk"));

        // The session is the caller's, named as the dataset chunk uploads name theirs: another user that sends the same
        // file id (or the name of a session of the dataset chunk uploads) reaches a session of their own, never this one
        await _chunks.OpenAsync(root, key, new ChunkManifest(chunkInfo.Path, chunkInfo.TotalFileSize,
            chunkInfo.TotalChunks, chunkSize, userId));
        var status = await _chunks.WriteAsync(root, key, chunkInfo.ChunkIndex, chunkStream, ct);

        // A large file is uploading: the batch is not abandoned while its chunks arrive (D-049)
        await _batches.TouchAsync(token, ct);

        return new ChunkUploadResultDto
        {
            FileId = chunkInfo.FileId,
            ReceivedChunk = chunkInfo.ChunkIndex,
            Success = true,
            IsComplete = status.Complete
        };
    }

    public async Task<UploadResultDto> FinalizeChunkedUpload(string token, string? fileId, string? path,
        CancellationToken ct = default)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("Missing token");

        if (string.IsNullOrWhiteSpace(fileId))
            throw new BadRequestException("Missing file ID");

        if (string.IsNullOrWhiteSpace(path))
            throw new BadRequestException("Missing path");

        // Verify the batch is valid and running
        var batch = await _batches.GetRunningAsync(token, ct);

        var orgSlug = batch.Dataset.Organization.Slug;
        var dsSlug = batch.Dataset.Slug;
        var ddb = DatasetOf(batch);

        // The path as the index stores it (D-040), and whether a file was there before the batch: it is replaced,
        // and a rollback keeps it (FR-XC-27)
        var target = ShareUploads.Target(_paths, ddb, path);

        // Verify the path is not already used
        if (batch.Entries.Any(item => item.Path == target.Path))
            throw new BadRequestException($"Entry with path '{target.Path}' already exists in this batch");

        _logger.LogInformation("Finalizing chunked upload for file ID {FileId} to path {Path}", fileId, target.Path);

        // The chunks were written into one file at their offsets: taking it is a rename, and its
        // hash was computed as the chunks arrived
        var root = ChunkedUploadManager.ChunksRoot(ddb);
        var userId = await _authManager.GetCurrentUserId();
        var key = ChunkedUploadManager.SessionKey(userId, fileId);
        var status = _chunks.GetStatus(root, key) ?? throw new NotFoundException($"Unknown upload '{fileId}'");
        ChunkedUploadManager.EnsureOwner(userId, status.Manifest, fileId);

        using var temp = _scratch.CreateForDataset(ddb.DatasetFolderPath, DatasetUploads.TempFileExtension);
        var hint = await _chunks.TakeAsync(root, key, temp.Path);
        temp.Release(); // the commit moves it into place or deletes it

        var added = await _objectsManager.CommitStreamedAsync(orgSlug, dsSlug, target.Path,
            new StreamedFile(temp.Path, hint.Size, hint));
        return await _batches.AddEntryAsync(batch, path, added, target.Replaced);
    }

    private string ChunksRoot(Batch batch) => ChunkedUploadManager.ChunksRoot(DatasetOf(batch));

    private IDDB DatasetOf(Batch batch) =>
        _ddbManager.Get(batch.Dataset.Organization.Slug, batch.Dataset.InternalRef);
}
