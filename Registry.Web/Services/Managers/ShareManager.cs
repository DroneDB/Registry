using System.Collections.Generic;
using System.IO;
using System.Threading;
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

    public Task<BatchDto> GetBatchInfo(string token, CancellationToken ct = default) => _store.GetBatchInfo(token, ct: ct);

    public Task<IEnumerable<BatchDto>> ListBatches(string orgSlug, string dsSlug, CancellationToken ct = default) => _store.ListBatches(orgSlug, dsSlug, ct: ct);

    public Task<ShareInitResultDto> Initialize(ShareInitDto parameters, CancellationToken ct = default) => _batches.Initialize(parameters, ct: ct);

    public Task<CommitResultDto> Commit(string token, CancellationToken ct = default) => _batches.Commit(token, ct: ct);

    public Task Rollback(string token, CancellationToken ct = default) => _batches.Rollback(token, ct: ct);

    public Task<(string OrgSlug, string DsSlug)> GetUploadTarget(string token, CancellationToken ct = default) => _uploads.GetUploadTarget(token, ct: ct);

    public Task<UploadResultDto> UploadStreamed(string token, string path, StreamedFile file, CancellationToken ct = default) =>
        _uploads.UploadStreamed(token, path, file, ct: ct);

    public Task<ChunkUploadResultDto> UploadChunk(string token, ChunkUploadDto chunkInfo, Stream chunkStream, CancellationToken ct = default) =>
        _chunked.UploadChunk(token, chunkInfo, chunkStream, ct: ct);

    public Task<UploadResultDto> FinalizeChunkedUpload(string token, string fileId, string path, CancellationToken ct = default) =>
        _chunked.FinalizeChunkedUpload(token, fileId, path, ct: ct);
}
