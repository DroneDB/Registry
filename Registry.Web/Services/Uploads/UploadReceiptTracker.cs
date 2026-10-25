#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Uploads;

/// <summary>
/// The receipt of an upload that carries a client id (out of <c>UploadManager</c>): a client that lost the
/// response asks for the outcome instead of sending the files again. Without a receipt store (a direct construction
/// in a test) no receipt is kept and every method is a no-op.
/// </summary>
public sealed class UploadReceiptTracker
{
    private readonly DistributedUploadReceiptStore? _receipts;
    private readonly ILogger<UploadReceiptTracker> _logger;

    public UploadReceiptTracker(ILogger<UploadReceiptTracker> logger, DistributedUploadReceiptStore? receipts = null)
    {
        _logger = logger;
        _receipts = receipts;
    }

    /// <summary>
    /// Starts the receipt of the upload of <paramref name="ctx"/> (<see cref="UploadRequestContext.UploadId"/> set):
    /// an upload already done puts its receipt on the context and returns false (nothing else must be done).
    /// </summary>
    /// <exception cref="ConflictException">An upload with that id is still running.</exception>
    public async Task<bool> BeginAsync(UploadRequestContext ctx)
    {
        if (ctx.UploadId == null || _receipts == null)
            return true;

        var receipt = await _receipts.GetAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId);
        switch (receipt?.State)
        {
            case UploadReceiptState.Done:
                ctx.Receipt = receipt;
                return false;
            case UploadReceiptState.Receiving or UploadReceiptState.Committing:
                throw new ConflictException($"Upload '{ctx.UploadId}' is still in progress");
        }

        await _receipts.SetAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId,
            new UploadReceipt(UploadReceiptState.Receiving, null, null, null, DateTime.UtcNow));
        return true;
    }

    /// <summary>Records <paramref name="state"/> (and the part results of a finished upload); a failure to record is logged.</summary>
    public async Task SetStateAsync(UploadRequestContext ctx, UploadReceiptState state,
        IReadOnlyList<UploadPartResultDto>? results)
    {
        if (ctx.UploadId == null || _receipts == null)
            return;

        try
        {
            await _receipts.SetAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId,
                new UploadReceipt(state, results, null, null, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot record upload {UploadId} as {State}", ctx.UploadId, state);
        }
    }

    /// <summary>Records that the request failed: a client that went away sends the file again (the attempt is forgotten), any other failure is kept with its status.</summary>
    public async Task FailAsync(UploadRequestContext ctx, Exception cause)
    {
        if (ctx.UploadId == null || _receipts == null)
            return;

        try
        {
            // A client that went away sends the file again: forget the attempt
            if (cause is OperationCanceledException or IOException or BadHttpRequestException)
            {
                await _receipts.RemoveAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId);
                return;
            }

            var d = ApiExceptionClassifier.Classify(cause, authenticated: ctx.User != null);
            await _receipts.SetAsync(ctx.OrgSlug, ctx.DsSlug, ctx.UploadId,
                new UploadReceipt(UploadReceiptState.Failed, null, d.StatusCode, d.Message, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot record the failure of upload {UploadId}", ctx.UploadId);
        }
    }

    /// <summary>The receipt of upload <paramref name="uploadId"/> of the dataset, or null.</summary>
    public async Task<UploadReceipt?> GetAsync(string orgSlug, string dsSlug, string uploadId) =>
        _receipts == null ? null : await _receipts.GetAsync(orgSlug, dsSlug, uploadId);
}
