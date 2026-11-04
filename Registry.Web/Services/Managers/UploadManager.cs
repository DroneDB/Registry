using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Uploads;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The upload pipeline: one context per request, pre-checks before the first byte,
/// hashing while streaming, one index batch, quota reservation and cache invalidation per request,
/// and receipts for clients that lost the response.
/// </summary>
public sealed partial class UploadManager
{
    private readonly IAuthManager _authManager;
    private readonly IDatasetAccessResolver _access;
    private readonly UploadQuotaGuard _quota;
    private readonly UploadReceiptTracker _receipts;
    private readonly UploadIndexer _indexer;
    private readonly UploadFiles _files;
    private readonly UploadCommitter _committer;

    public UploadManager(IAuthManager authManager, IDatasetAccessResolver access, UploadQuotaGuard quota,
        UploadReceiptTracker receipts, UploadIndexer indexer, UploadFiles files, UploadCommitter committer)
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

    /// <summary>
    /// Resolves the dataset, checks write access, reserves <paramref name="expectedBytes"/> of quota
    /// and registers the upload: everything that can refuse it before its first byte. With an
    /// <paramref name="uploadId"/> whose upload is already done, <see cref="UploadRequestContext.Receipt"/>
    /// holds its results and nothing else must be done.
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.ConflictException">An upload with that id is still running.</exception>
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

    /// <summary>
    /// The normalized dataset path a file uploaded to <paramref name="path"/> is stored at; throws when
    /// no file can be uploaded there (before streaming it).
    /// </summary>
    /// <exception cref="Registry.Web.Exceptions.InvalidDatasetPathException">Not a dataset path (NUL, absolute, "..", reserved).</exception>
    /// <exception cref="Registry.Web.Exceptions.ExtensionBlockedException">The import policy blocks the extension.</exception>
    public string ValidatePath(string path) => _files.ValidatePath(path);

    /// <summary>Streams one file part to a temp file in the dataset, hashing it on the way.</summary>
    public Task<StreamedFile> StreamAsync(UploadRequestContext ctx, Stream body, CancellationToken ct = default) =>
        _files.StreamAsync(ctx, body, ct);

    /// <summary>Commits a single file: moves it into place and indexes it. Throws on failure.</summary>
    public async Task<EntryDto> CommitAsync(UploadRequestContext ctx, string path, StreamedFile file)
    {
        var outcomes = await _committer.CommitAsync(ctx, [new StreamedPart(path, file)]);
        var outcome = outcomes[0];
        if (outcome.Error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(outcome.Error);
        return outcome.Entry!;
    }

    /// <summary>
    /// Commits several files with one quota reservation, one index batch and one invalidation.
    /// Each file gets its own result; one that fails does not fail the others.
    /// </summary>
    public async Task<IReadOnlyList<UploadPartResultDto>> CommitAsync(UploadRequestContext ctx,
        IReadOnlyList<StreamedPart> parts)
    {
        var outcomes = await _committer.CommitAsync(ctx, parts);
        return outcomes.Select(o => o.ToDto(authenticated: ctx.User != null)).ToArray();
    }

    /// <summary>Deletes the temp file of a part that will not be committed (the request failed while reading it).</summary>
    public void Discard(StreamedFile file) => _files.Discard(file);

    public Task FailAsync(UploadRequestContext ctx, Exception error) => _receipts.FailAsync(ctx, error);

    /// <summary>The outcome of an upload by its id, or null when unknown (write access required).</summary>
    public async Task<UploadReceipt?> GetReceiptAsync(string orgSlug, string dsSlug, string uploadId,
        CancellationToken ct = default)
    {
        if (!IsValidUploadId(uploadId))
            throw new BadRequestException("Invalid upload id");

        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        return await _receipts.GetAsync(orgSlug, dsSlug, uploadId);
    }
}
