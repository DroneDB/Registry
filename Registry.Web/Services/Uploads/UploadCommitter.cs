#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Exceptions;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Uploads;

/// <summary>The commit of an upload request (RQ-12, out of <c>UploadManager</c>): the streamed files become entries of the dataset.</summary>
public interface IUploadCommitter
{
    /// <summary>
    /// Commits <paramref name="parts"/> with one quota decision, one index batch and one invalidation: validates
    /// each file, moves it into place, indexes the batch, quarantines what failed to index, finalizes the rest and
    /// settles the receipt. A part that fails does not fail the others; a request-level failure (quota, a native
    /// outage) is every unsettled part's outcome. Never throws.
    /// </summary>
    Task<IReadOnlyList<UploadPartOutcome>> CommitAsync(UploadRequestContext ctx, IReadOnlyList<StreamedPart> parts);
}

/// <summary>Default <see cref="IUploadCommitter"/>.</summary>
public sealed class UploadCommitter : IUploadCommitter
{
    private readonly IUploadFiles _files;
    private readonly IUploadQuotaGuard _quota;
    private readonly IUploadReceiptTracker _receipts;
    private readonly IUploadIndexer _indexer;
    private readonly IUploadFinalizer _finalizer;
    private readonly ILogger<UploadCommitter> _logger;

    public UploadCommitter(IUploadFiles files, IUploadQuotaGuard quota, IUploadReceiptTracker receipts,
        IUploadIndexer indexer, IUploadFinalizer finalizer, ILogger<UploadCommitter> logger)
    {
        _files = files;
        _quota = quota;
        _receipts = receipts;
        _indexer = indexer;
        _finalizer = finalizer;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UploadPartOutcome>> CommitAsync(UploadRequestContext ctx,
        IReadOnlyList<StreamedPart> parts)
    {
        _logger.LogInformation("Committing {Count} uploaded file(s) to '{OrgSlug}/{DsSlug}'", parts.Count,
            ctx.OrgSlug, ctx.DsSlug);

        var outcomes = new UploadPartOutcome?[parts.Count];
        var moved = new List<(int Index, MovedUploadPart Part)>();
        try
        {
            await _receipts.SetStateAsync(ctx, UploadReceiptState.Committing, null);

            // Validation first: a refused file is never moved into the dataset; a valid one is moved
            // to its normalized path
            var valid = new List<int>();
            var sizes = new long[parts.Count];
            var targets = new string[parts.Count];
            for (var i = 0; i < parts.Count; i++)
            {
                try
                {
                    _files.ValidateTempFile(ctx, parts[i].File.TempPath);
                    targets[i] = _files.ValidatePath(parts[i].Path);
                    sizes[i] = _files.ActualSize(parts[i].File);
                    valid.Add(i);
                }
                catch (Exception ex)
                {
                    outcomes[i] = new UploadPartOutcome(parts[i].Path, null, ex);
                }
            }

            // One quota decision for the request, on the authoritative sizes on disk
            var bytes = valid.Sum(i => sizes[i]);
            await ReserveAsync(ctx, bytes);

            foreach (var i in valid)
            {
                var part = parts[i] with { Path = targets[i] };
                moved.Add((i, new MovedUploadPart(part, _files.MoveIntoDataset(ctx, part))));
            }

            await _indexer.IndexAsync(ctx, moved.Select(m => m.Part).ToArray());

            foreach (var (i, m) in moved)
            {
                if (m.Error != null)
                {
                    // Retryable index contention: the file on disk is healthy and was never committed
                    // to the index; reconciliation + client retry (503 + Retry-After) is enough.
                    // Anything else: quarantine instead of leaving an untracked file behind.
                    if (m.Error is not TransientException and not DdbBusyException)
                        _files.Quarantine(ctx, m.Part.Path, m.LocalPath, m.Error);
                    outcomes[i] = new UploadPartOutcome(m.Part.Path, null, m.Error);
                }
            }

            var indexed = moved.Where(m => m.Part.Entry != null).ToArray();
            if (indexed.Length > 0)
            {
                ctx.Reservation?.Resize(indexed.Sum(m => sizes[m.Index]));
                ctx.Reservation?.Commit();
            }

            // Post-index finalization: the entries are committed, failures here never quarantine
            await _finalizer.FinalizeAsync(ctx, indexed.Select(m => m.Part).ToArray());
            foreach (var (i, m) in indexed)
                outcomes[i] = new UploadPartOutcome(m.Part.Path, m.Entry!.ToDto(), null);
        }
        catch (Exception ex)
        {
            // Request-level failure (quota, a native outage): every file not settled shares it
            for (var i = 0; i < parts.Count; i++)
                outcomes[i] ??= new UploadPartOutcome(parts[i].Path, null, ex);
        }
        finally
        {
            // Whatever happened, this request enqueues nothing more: the index lane must not wait for it
            ctx.Ticket?.End();

            // A part that was never moved (validation or quota failure) leaves no temp file behind;
            // a path outside this dataset's uploads folder is never ours to delete
            foreach (var part in parts)
                _files.DeleteLeftover(ctx, part.File);
        }

        var results = outcomes.Select((o, i) => o ?? new UploadPartOutcome(parts[i].Path, null,
            new InvalidOperationException("Upload part was not processed"))).ToArray();

        var dtos = results.Select(r => r.ToDto(authenticated: ctx.User != null)).ToArray();
        if (results.All(r => r.Error != null))
            await _receipts.FailAsync(ctx, results[0].Error!);
        else
            await _receipts.SetStateAsync(ctx, UploadReceiptState.Done, dtos);
        return results;
    }

    private async Task ReserveAsync(UploadRequestContext ctx, long bytes)
    {
        if (ctx.Reservation == null)
            ctx.Reservation = await _quota.ReserveAsync(ctx.QuotaUserId, bytes);
        else
            ctx.Reservation.Resize(bytes);
    }
}
