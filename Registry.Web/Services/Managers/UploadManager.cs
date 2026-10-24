#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Default <see cref="IUploadManager"/> (what the controllers call) over the services RQ-12 split it into (15
/// parameters before): the files of a request (<see cref="IUploadFiles"/>), the receipt of an upload with a client
/// id (<see cref="IUploadReceiptTracker"/>), the index step (<see cref="IUploadIndexer"/>) and the commit
/// (<see cref="IUploadCommitter"/>); this class resolves the request (access, user, quota, receipt, index lane)
/// before the first byte.
/// </summary>
public sealed partial class UploadManager : IUploadManager
{
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly IUploadQuotaGuard _quota;
    private readonly IUploadReceiptTracker _receipts;
    private readonly IUploadIndexer _indexer;
    private readonly IUploadFiles _files;
    private readonly IUploadCommitter _committer;

    public UploadManager(IAuthManager authManager, IDatasetAccessResolver access, IUploadQuotaGuard quota,
        IUploadReceiptTracker receipts, IUploadIndexer indexer, IUploadFiles files, IUploadCommitter committer)
    {
        _authManager = authManager;
        _access = access;
        _quota = quota;
        _receipts = receipts;
        _indexer = indexer;
        _files = files;
        _committer = committer;
    }

    // Client-generated ids: GUIDs or similar tokens, never anything that could shape a cache key
    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex UploadIdPattern();

    public static bool IsValidUploadId(string? uploadId) => uploadId != null && UploadIdPattern().IsMatch(uploadId);

    public async Task<UploadRequestContext> BeginAsync(string orgSlug, string dsSlug, long? expectedBytes = null,
        string? uploadId = null, CancellationToken ct = default)
    {
        if (uploadId != null && !IsValidUploadId(uploadId))
            throw new BadRequestException("Invalid upload id: use 8 to 64 letters, digits, '-' or '_'");

        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);
        var ds = access.Dataset;
        var ddb = access.Ddb;

        var user = await _authManager.GetCurrentUser();
        var ctx = new UploadRequestContext(orgSlug, dsSlug, ds, ddb, user, uploadId);

        try
        {
            // An upload already done answers from its receipt: nothing else to reserve
            if (!await _receipts.BeginAsync(ctx))
                return ctx;

            // Content-Length is known at byte 0: refuse an upload over quota before it is sent
            if (expectedBytes is > 0)
                ctx.Reservation = await _quota.ReserveAsync(ctx.QuotaUserId, expectedBytes.Value);

            ctx.Ticket = _indexer.BeginUpload(ctx);

            return ctx;
        }
        catch (Exception ex)
        {
            await FailAsync(ctx, ex);
            ctx.Dispose();
            throw;
        }
    }

    public string ValidatePath(UploadRequestContext ctx, string path) => _files.ValidatePath(path);

    public Task<StreamedFile> StreamAsync(UploadRequestContext ctx, Stream body, CancellationToken ct = default) =>
        _files.StreamAsync(ctx, body, ct);

    public async Task<EntryDto> CommitAsync(UploadRequestContext ctx, string path, StreamedFile file)
    {
        var outcomes = await _committer.CommitAsync(ctx, [new StreamedPart(path, file)]);
        var outcome = outcomes[0];
        if (outcome.Error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(outcome.Error);
        return outcome.Entry!;
    }

    public async Task<IReadOnlyList<UploadPartResultDto>> CommitAsync(UploadRequestContext ctx,
        IReadOnlyList<StreamedPart> parts)
    {
        var outcomes = await _committer.CommitAsync(ctx, parts);
        return outcomes.Select(o => o.ToDto(authenticated: ctx.User != null)).ToArray();
    }

    public void Discard(StreamedFile file) => _files.Discard(file);

    public Task FailAsync(UploadRequestContext ctx, Exception error) => _receipts.FailAsync(ctx, error);

    public async Task<UploadReceipt?> GetReceiptAsync(string orgSlug, string dsSlug, string uploadId,
        CancellationToken ct = default)
    {
        if (!IsValidUploadId(uploadId))
            throw new BadRequestException("Invalid upload id");

        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        return await _receipts.GetAsync(orgSlug, dsSlug, uploadId);
    }
}
