using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Share/upload batch workflow interface (init, upload, commit, rollback, chunked uploads).
/// </summary>
public interface IShareManager
{
    public Task<ShareInitResultDto> Initialize(ShareInitDto parameters);
    public Task<UploadResultDto> Upload(string token, string path, byte[] data);
    public Task<UploadResultDto> Upload(string token, string path, Stream stream);

    /// <summary>
    /// Resolves the organization/dataset of a running batch so the caller can stream an upload
    /// directly onto the dataset's storage volume.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <returns>The organization and dataset slugs of the running batch.</returns>
    public Task<(string OrgSlug, string DsSlug)> GetUploadTarget(string token);

    /// <summary>
    /// Finalizes a streamed share upload produced via <c>IObjectsManager.StreamToTempAsync</c>.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <param name="path">The destination path within the dataset.</param>
    /// <param name="tempFilePath">The temp file produced by <c>IObjectsManager.StreamToTempAsync</c>.</param>
    /// <param name="bytes">The number of bytes written while streaming (used for the storage-quota check).</param>
    /// <returns>The upload result.</returns>
    public Task<UploadResultDto> UploadStreamed(string token, string path, string tempFilePath, long bytes);

    public Task<CommitResultDto> Commit(string token);
    public Task Rollback(string token);
    Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug);
    public Task<bool> IsPathAllowed(string token, string path);
    public Task<IsBatchReadyResult> IsBatchReady(string token);

    public Task<BatchDto> GetBatchInfo(string token);

    // Chunked upload methods
    public Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream);
    public Task<UploadResultDto> FinalizeChunkedUpload(string token, string fileId, string path);
}