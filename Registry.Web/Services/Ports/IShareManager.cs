using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Share/upload batch workflow interface (init, upload, commit, rollback, chunked uploads).
/// </summary>
public interface IShareManager
{
    public Task<ShareInitResultDto> Initialize(ShareInitDto parameters, CancellationToken ct = default);
    public Task<UploadResultDto> Upload(string token, string path, byte[] data, CancellationToken ct = default);
    public Task<UploadResultDto> Upload(string token, string path, Stream stream, CancellationToken ct = default);

    /// <summary>
    /// Resolves the organization/dataset of a running batch so the caller can stream an upload
    /// directly onto the dataset's storage volume.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <returns>The organization and dataset slugs of the running batch.</returns>
    public Task<(string OrgSlug, string DsSlug)> GetUploadTarget(string token, CancellationToken ct = default);

    /// <summary>
    /// Finalizes a streamed share upload produced via <c>IObjectsManager.StreamToTempAsync</c>.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <param name="path">The destination path within the dataset.</param>
    /// <param name="file">The temp file produced by <c>IObjectsManager.StreamToTempAsync</c>.</param>
    /// <returns>The upload result.</returns>
    public Task<UploadResultDto> UploadStreamed(string token, string path, Registry.Web.Utilities.StreamedFile file, CancellationToken ct = default);

    public Task<CommitResultDto> Commit(string token, CancellationToken ct = default);
    public Task Rollback(string token, CancellationToken ct = default);
    Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug, CancellationToken ct = default);
    public Task<bool> IsPathAllowed(string token, string path, CancellationToken ct = default);
    public Task<IsBatchReadyResult> IsBatchReady(string token, CancellationToken ct = default);

    public Task<BatchDto> GetBatchInfo(string token, CancellationToken ct = default);

    // Chunked upload methods
    public Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream, CancellationToken ct = default);
    public Task<UploadResultDto> FinalizeChunkedUpload(string token, string fileId, string path, CancellationToken ct = default);
}