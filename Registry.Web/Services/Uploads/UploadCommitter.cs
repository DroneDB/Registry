using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Uploads;

/// <summary>The commit of an upload request (out of <c>UploadManager</c>): the streamed files become entries of the dataset.</summary>
public sealed class UploadCommitter
{
    private readonly UploadFiles _files;
    private readonly UploadQuotaGuard _quota;
    private readonly UploadReceiptTracker _receipts;
    private readonly UploadIndexer _indexer;
    private readonly UploadFinalizer _finalizer;
    private readonly ILogger<UploadCommitter> _logger;

    public UploadCommitter(UploadFiles files, UploadQuotaGuard quota, UploadReceiptTracker receipts,
        UploadIndexer indexer, UploadFinalizer finalizer, ILogger<UploadCommitter> logger)
    {
        _files = files;
        _quota = quota;
        _receipts = receipts;
        _indexer = indexer;
        _finalizer = finalizer;
        _logger = logger;
    }

    /// <summary>
    /// Commits <paramref name="parts"/> with one quota decision, one index batch and one invalidation: validates
    /// each file, moves it into place, indexes the batch, quarantines what failed to index, finalizes the rest and
    /// settles the receipt. A part that fails does not fail the others; a request-level failure (quota, a native
    /// outage) is every unsettled part's outcome. A file whose index write is still running when the wait ends stays
    /// in place until the queue tells its outcome (<see cref="LateIndexWrites"/>). Never throws.
    /// </summary>
    public async Task<IReadOnlyList<UploadPartOutcome>> CommitAsync(UploadRequestContext ctx,
        IReadOnlyList<StreamedPart> parts)
    {
        _logger.LogInformation("Committing {Count} uploaded file(s) to '{OrgSlug}/{DsSlug}'", parts.Count,
            ctx.OrgSlug, ctx.DsSlug);

        var outcomes = new UploadPartOutcome?[parts.Count];
        var moved = new List<(int Index, MovedUploadPart Part)>();
        var sizes = new long[parts.Count];
        var settled = false;
        LateIndexWrites? late = null;
        try
        {
            await _receipts.SetStateAsync(ctx, UploadReceiptState.Committing, null);

            // Validation first: a refused file is never moved into the dataset; a valid one is moved
            // to its normalized path, in the letter case of the entry it replaces (D-040). The name is checked
            // again here, so a file written since the part's first byte still refuses it
            var valid = new List<int>();
            var targets = new string[parts.Count];
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i].Refusal is { } refusal)
                {
                    outcomes[i] = new UploadPartOutcome(parts[i].Path, null, refusal);
                    continue;
                }

                try
                {
                    _files.ValidateTempFile(ctx, parts[i].File.TempPath);
                    targets[i] = _files.ResolveTarget(ctx, parts[i].Path, parts[i].Overwrite);
                    // Two parts of one request with one name: the second one clashes unless it overwrites
                    if (!claimed.Add(targets[i]) && !parts[i].Overwrite)
                        throw new NameClashException(targets[i]);
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
                try
                {
                    moved.Add((i, _files.MoveIntoDataset(ctx, part)));
                }
                catch (NameClashException clash)
                {
                    // Lost the name to another writer after the check: this part only
                    outcomes[i] = new UploadPartOutcome(parts[i].Path, null, clash);
                }
            }

            var placed = moved.Select(m => m.Part).ToArray();
            late = new LateIndexWrites(ctx, placed, _files, _logger);
            await _indexer.IndexAsync(ctx, placed, late.Tell);

            foreach (var (i, m) in moved)
            {
                if (m.Error != null)
                {
                    _files.DropUnindexed(ctx, m, m.Error);
                    outcomes[i] = new UploadPartOutcome(m.Part.Path, null, m.Error);
                }
            }

            // The quota keeps the files that stay in the dataset: the indexed ones and those whose index write is
            // still running (the batch lists them when it commits)
            settled = true;
            SettleReservation(ctx, moved, sizes, late);

            var indexed = moved.Where(m => m.Part.Entry != null).ToArray();

            // Post-index finalization: the entries are committed, failures here never quarantine
            await _finalizer.FinalizeAsync(ctx, indexed.Select(m => m.Part).ToArray());
            foreach (var (i, m) in indexed)
                outcomes[i] = new UploadPartOutcome(m.Part.Path, m.Entry!.ToDto(), null);
        }
        catch (Exception ex)
        {
            // Request-level failure (quota, a native outage, a canceled index write): a file already
            // moved into the dataset that has no entry must not stay as an unindexed file that blocks the retry,
            // unless its index write may still commit
            foreach (var (i, m) in moved)
                if (outcomes[i] == null && m.Entry == null)
                    _files.DropUnindexed(ctx, m, ex);

            if (!settled)
                SettleReservation(ctx, moved, sizes, late, ex);

            // Every file not settled shares it
            for (var i = 0; i < parts.Count; i++)
                outcomes[i] ??= new UploadPartOutcome(parts[i].Path, null, ex);
        }
        finally
        {
            // Whatever happened, this request enqueues nothing more: the index lane must not wait for it
            ctx.Ticket?.End();

            // A part that was never moved (validation or quota failure) leaves no temp file behind;
            // a path outside this dataset's uploads folder is never ours to delete. A file still waiting for its
            // index outcome is left to that outcome
            var awaiting = moved.Where(m => LateIndexWrites.Awaits(m.Part)).Select(m => m.Index).ToHashSet();
            for (var i = 0; i < parts.Count; i++)
                if (!awaiting.Contains(i))
                    _files.DeleteLeftover(ctx, parts[i].File);
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

    // The reservation becomes the size of the files that stay in the dataset (indexed, or whose index write may
    // still commit; a file without its own error shares the request's); with none it is released when the request
    // ends. The size of the files whose outcome the queue tells later moves to a reservation of their own, which
    // keeps those that stay once it is told (LateIndexWrites)
    private static void SettleReservation(UploadRequestContext ctx, List<(int Index, MovedUploadPart Part)> moved,
        long[] sizes, LateIndexWrites? late, Exception? requestError = null)
    {
        var awaited = new Dictionary<int, long>();
        for (var item = 0; item < moved.Count; item++)
            if (LateIndexWrites.Awaits(moved[item].Part))
                awaited[item] = sizes[moved[item].Index];

        var reservation = ctx.Reservation;
        late?.Await(awaited, reservation == null || awaited.Count == 0 ? null : reservation.Split(awaited.Values.Sum()));

        var staying = moved.Where(m => !LateIndexWrites.Awaits(m.Part) &&
                                       (m.Part.Entry != null || UploadFiles.OutcomeUnknown(m.Part.Error ?? requestError)))
            .ToArray();
        if (staying.Length == 0 || reservation == null)
            return;

        reservation.Resize(staying.Sum(m => sizes[m.Index]));
        reservation.Commit();
    }

    private async Task ReserveAsync(UploadRequestContext ctx, long bytes)
    {
        if (ctx.Reservation == null)
            ctx.Reservation = await _quota.ReserveAsync(ctx.QuotaOwnerId, bytes);
        else
            ctx.Reservation.Resize(bytes);
    }
}
