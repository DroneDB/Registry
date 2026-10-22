using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Shares;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The share/upload batch API (<see cref="IShareManager"/>, what <c>ShareController</c> calls) over the share services
/// RQ-12 split it into (16 parameters, two optional, before): the batches, the uploads, the chunked uploads.
/// </summary>
public class ShareManager : IShareManager
{
    private readonly IShareBatchStore _store;
    private readonly IShareBatches _batches;
    private readonly IShareUploads _uploads;
    private readonly IShareChunkedUploads _chunked;

    public ShareManager(IShareBatchStore store, IShareBatches batches, IShareUploads uploads,
        IShareChunkedUploads chunked)
    {
        _store = store;
        _batches = batches;
        _uploads = uploads;
        _chunked = chunked;
    }

    public Task<BatchDto> GetBatchInfo(string token) => _store.GetBatchInfo(token);

    public Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug) => _store.ListBatches(orgSlug, dsSlug);

    public Task<bool> IsPathAllowed(string token, string path) => _store.IsPathAllowedAsync(token, path);

    public Task<IsBatchReadyResult> IsBatchReady(string token) => _store.IsReadyAsync(token);

    public Task<ShareInitResultDto> Initialize(ShareInitDto parameters) => _batches.Initialize(parameters);

    public Task<CommitResultDto> Commit(string token) => _batches.Commit(token);

    public Task Rollback(string token) => _batches.Rollback(token);

    public Task<UploadResultDto> Upload(string token, string path, byte[] data) => _uploads.Upload(token, path, data);

    public Task<UploadResultDto> Upload(string token, string path, Stream stream) => _uploads.Upload(token, path, stream);

    public Task<(string OrgSlug, string DsSlug)> GetUploadTarget(string token) => _uploads.GetUploadTarget(token);

    public Task<UploadResultDto> UploadStreamed(string token, string path, StreamedFile file) =>
        _uploads.UploadStreamed(token, path, file);

    public Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream) =>
        _chunked.UploadChunk(token, chunkInfo, chunkStream);

    public Task<UploadResultDto> FinalizeChunkedUpload(string token, string fileId, string path) =>
        _chunked.FinalizeChunkedUpload(token, fileId, path);
}
