using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Shares;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Share/upload batch workflow interface (init, upload, commit, rollback, chunked uploads).
/// </summary>
public class ShareManager
{
    private readonly ShareBatchStore _store;
    private readonly ShareBatches _batches;
    private readonly ShareUploads _uploads;
    private readonly ShareChunkedUploads _chunked;

    public ShareManager(ShareBatchStore store, ShareBatches batches, ShareUploads uploads,
        ShareChunkedUploads chunked)
    {
        _store = store;
        _batches = batches;
        _uploads = uploads;
        _chunked = chunked;
    }

    public Task<BatchDto> GetBatchInfo(string token, CancellationToken ct = default) => _store.GetBatchInfo(token, ct: ct);

    public Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug, CancellationToken ct = default) => _store.ListBatches(orgSlug, dsSlug, ct: ct);

    public Task<ShareInitResultDto> Initialize(ShareInitDto parameters, CancellationToken ct = default) => _batches.Initialize(parameters, ct: ct);

    public Task<CommitResultDto> Commit(string token, CancellationToken ct = default) => _batches.Commit(token, ct: ct);

    public Task Rollback(string token, CancellationToken ct = default) => _batches.Rollback(token, ct: ct);

    /// <summary>
    /// Resolves the organization/dataset of a running batch so the caller can stream an upload
    /// directly onto the dataset's storage volume.
    /// </summary>
    /// <param name="token">The share batch token.</param>
    /// <returns>The organization and dataset slugs of the running batch.</returns>
    public Task<(string OrgSlug, string DsSlug)> GetUploadTarget(string token, CancellationToken ct = default) => _uploads.GetUploadTarget(token, ct: ct);

    public Task<UploadResultDto> UploadStreamed(string token, string path, StreamedFile file, CancellationToken ct = default) =>
        _uploads.UploadStreamed(token, path, file, ct: ct);

    public Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream, CancellationToken ct = default) =>
        _chunked.UploadChunk(token, chunkInfo, chunkStream, ct: ct);

    public Task<UploadResultDto> FinalizeChunkedUpload(string token, string fileId, string path, CancellationToken ct = default) =>
        _chunked.FinalizeChunkedUpload(token, fileId, path, ct: ct);
}
