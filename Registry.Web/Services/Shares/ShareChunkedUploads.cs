using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>The large files of a share batch, in chunks: written at their offsets next to the dataset, taken by a rename when complete.</summary>
public interface IShareChunkedUploads
{
    Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream);
    Task<UploadResultDto> FinalizeChunkedUpload(string token, string fileId, string path);
}

/// <summary>Default <see cref="IShareChunkedUploads"/> (RQ-12, out of <c>ShareManager</c>).</summary>
public sealed class ShareChunkedUploads : IShareChunkedUploads
{
    private readonly IShareBatchStore _batches;
    private readonly IStorageQuota _quota;
    private readonly IChunkStore _chunks;
    private readonly IScratchFileProvider _scratch;
    private readonly IDdbManager _ddbManager;
    private readonly IObjectsManager _objectsManager;
    private readonly ILogger<ShareChunkedUploads> _logger;

    public ShareChunkedUploads(IShareBatchStore batches, IStorageQuota quota, IChunkStore chunks, IScratchFileProvider scratch, IDdbManager ddbManager, IObjectsManager objectsManager, ILogger<ShareChunkedUploads> logger)
    {
        _batches = batches;
        _quota = quota;
        _chunks = chunks;
        _scratch = scratch;
        _ddbManager = ddbManager;
        _objectsManager = objectsManager;
        _logger = logger;
    }

    public async Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream)
    {
        // Validate input parameters
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("Missing token");

        if (chunkInfo == null)
            throw new BadRequestException("Missing chunk information");

        if (chunkStream == null || !chunkStream.CanRead)
            throw new BadRequestException("Invalid chunk data stream");

        if (string.IsNullOrWhiteSpace(chunkInfo.Path))
            throw new BadRequestException("Missing path");

        // Verify the batch is valid and running
        var batch = await _batches.GetRunningAsync(token);

        // Check if path is allowed for this batch
        if (!await _batches.IsPathAllowedAsync(token, chunkInfo.Path))
            throw new BadRequestException($"Path '{chunkInfo.Path}' is not allowed in this batch");

        var root = ChunksRoot(batch);
        if (_chunks.GetStatus(root, chunkInfo.FileId) == null)
        {
            // A new upload: check the user has room for the whole file before its first byte
            await _quota.CheckCurrentUserStorage(chunkInfo.TotalFileSize);
        }

        _logger.LogDebug("Processing chunk {ChunkIndex}/{TotalChunks} for file {FileName} (FileId: {FileId})",
            chunkInfo.ChunkIndex, chunkInfo.TotalChunks, chunkInfo.FileName, chunkInfo.FileId);

        // Every chunk but the last has the same size; a single chunk is the whole file
        var chunkSize = chunkInfo.ChunkSize ?? (chunkInfo.TotalChunks == 1
            ? chunkInfo.TotalFileSize
            : throw new BadRequestException("ChunkSize is required when a file is sent in more than one chunk"));

        _chunks.Open(root, chunkInfo.FileId, new ChunkManifest(chunkInfo.Path, chunkInfo.TotalFileSize,
            chunkInfo.TotalChunks, chunkSize, null));
        var status = await _chunks.WriteAsync(root, chunkInfo.FileId, chunkInfo.ChunkIndex, chunkStream);

        return new ChunkUploadResultDto
        {
            FileId = chunkInfo.FileId,
            ReceivedChunk = chunkInfo.ChunkIndex,
            Success = true,
            IsComplete = status.Complete
        };
    }

    public async Task<UploadResultDto> FinalizeChunkedUpload(string token, string fileId, string path)
    {
        // Validate parameters
        if (string.IsNullOrWhiteSpace(token))
            throw new BadRequestException("Missing token");

        if (string.IsNullOrWhiteSpace(fileId))
            throw new BadRequestException("Missing file ID");

        if (string.IsNullOrWhiteSpace(path))
            throw new BadRequestException("Missing path");

        // Verify the batch is valid and running
        var batch = await _batches.GetRunningAsync(token);

        // Verify the path is not already used
        if (batch.Entries.Any(item => item.Path == path))
            throw new BadRequestException($"Entry with path '{path}' already exists in this batch");

        _logger.LogInformation("Finalizing chunked upload for file ID {FileId} to path {Path}", fileId, path);

        var orgSlug = batch.Dataset.Organization.Slug;
        var dsSlug = batch.Dataset.Slug;

        // The chunks were written into one file at their offsets: taking it is a rename, and its
        // hash was computed as the chunks arrived
        var ddb = DatasetOf(batch);
        var root = ChunkedUploadManager.ChunksRoot(ddb);
        using var temp = _scratch.CreateForDataset(ddb.DatasetFolderPath, DatasetUploads.TempFileExtension);
        var hint = await _chunks.TakeAsync(root, fileId, temp.Path);
        temp.Release(); // the commit moves it into place or deletes it

        var added = await _objectsManager.CommitStreamedAsync(orgSlug, dsSlug, path,
            new StreamedFile(temp.Path, hint.Size, hint));
        return await _batches.AddEntryAsync(batch, path, added);
    }

    private string ChunksRoot(Batch batch) => ChunkedUploadManager.ChunksRoot(DatasetOf(batch));

    private IDDB DatasetOf(Batch batch) =>
        _ddbManager.Get(batch.Dataset.Organization.Slug, batch.Dataset.InternalRef);
}
