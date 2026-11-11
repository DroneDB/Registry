using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Shares;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Recurring job that rolls back the abandoned /upload share batches (FR-UPL-01, §8.3, D-049): a batch still Running
/// whose last upload activity (a chunk or a file, <see cref="Batch.LastActivity"/>) is older than
/// <see cref="AbandonedAfter"/> (the user reloaded the page, closed the tab or lost the connection instead of pressing
/// Cancel). The batch ends first, only if it is still running and idle, so a commit that came first wins and an upload
/// that arrives meanwhile finds it closed; then the dataset that the batch created is deleted by the system
/// (<see cref="DatasetRemoval.RemoveAsSystemAsync"/>) while it holds only the batch's files. Otherwise (entries added
/// outside the batch, <c>ddb share</c> to an existing dataset) only the files the batch created are removed; a file it
/// replaced stays (FR-XC-27). A batch from before the upgrade that records replaced files (no
/// <see cref="Batch.LastActivity"/>) on a dataset it did not create only ends: which of its files were there before is
/// unknown, so none is deleted (<see cref="ShareBatchRollback.ReplacedUnknown"/>). The first run also closes the
/// batches left before the fix. It runs where the other recurring jobs run (also on the processing nodes), so it
/// needs only the services of every job host.
/// </summary>
/// <remarks>
/// One run at a time across the hosts (timeout 0: the run in progress does the work). A batch that cannot be rolled back
/// stays Running and is tried again at the next run.
/// </remarks>
[AutomaticRetry(Attempts = 0)]
[DisableConcurrentExecution(timeoutInSeconds: 0)]
public class AbandonedShareBatchesService
{
    /// <summary>The time without an upload after which a batch is abandoned: a constant, not a setting (§8.3).</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

    /// <summary>When the job runs: every hour (the 24 hours are the rule, the cron only says how soon it is applied).</summary>
    public const string Cron = "0 * * * *";

    private readonly RegistryContext _db;
    private readonly DatasetRemoval _removal;
    private readonly IDdbManager _ddbManager;
    private readonly TaskEntryChanges _changes;
    private readonly ILogger<AbandonedShareBatchesService> _logger;
    private readonly TimeProvider _time;

    public AbandonedShareBatchesService(RegistryContext db, DatasetRemoval removal, IDdbManager ddbManager,
        TaskEntryChanges changes, ILogger<AbandonedShareBatchesService> logger, TimeProvider? time = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _removal = removal ?? throw new ArgumentNullException(nameof(removal));
        _ddbManager = ddbManager ?? throw new ArgumentNullException(nameof(ddbManager));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    public async Task RollbackAbandonedAsync(PerformContext? context, CancellationToken cancellationToken)
    {
        var console = JobConsole.For(_logger, context);
        var cutoff = _time.GetUtcNow().UtcDateTime - AbandonedAfter;

        var tokens = await _db.Batches.AsNoTracking()
            .Where(AbandonedSince(cutoff))
            .Select(b => b.Token)
            .ToListAsync(cancellationToken);

        var rolledBack = 0;
        var failed = 0;
        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await RollbackAsync(token, cutoff, cancellationToken))
                    rolledBack++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                console.Error(ex, "Cannot roll back the abandoned share batch {Token}", token);
            }
        }

        console.Info("Abandoned share batches: {RolledBack} rolled back, {Failed} failed, {Found} found", rolledBack,
            failed, tokens.Count);
    }

    /// <summary>
    /// A running batch with no upload activity since <paramref name="cutoff"/>: its last activity (a chunk, a file:
    /// <see cref="Batch.LastActivity"/>), or for a batch from before that column its start, and its newest entry are
    /// older.
    /// </summary>
    private static Expression<Func<Batch, bool>> AbandonedSince(DateTime cutoff) =>
        b => b.Status == BatchStatus.Running && (b.LastActivity ?? b.Start) < cutoff &&
             !b.Entries.Any(e => e.AddedOn >= cutoff);

    private async Task<bool> RollbackAsync(string token, DateTime cutoff, CancellationToken ct)
    {
        var batch = await _db.Batches.AsNoTracking()
            .Include(b => b.Entries)
            .Include(b => b.Dataset).ThenInclude(d => d.Organization)
            .FirstOrDefaultAsync(b => b.Token == token, ct);
        if (batch is null)
            return false;

        // The batch ends first, only while it is still running and still idle (checked in the same statement): a commit,
        // a rollback of the user or a chunk that came since the query wins, and an upload that comes now finds it closed
        var closed = await _db.Batches.Where(b => b.Token == token).Where(AbandonedSince(cutoff))
            .ExecuteUpdateAsync(set => set.SetProperty(b => b.Status, BatchStatus.Rolledback)
                .SetProperty(b => b.End, _time.GetUtcNow().UtcDateTime), CancellationToken.None);
        if (closed == 0)
            return false;

        var dataset = batch.Dataset;
        var orgSlug = dataset.Organization.Slug;
        var internalRef = dataset.InternalRef;
        // A file the batch replaced was there before it and stays (FR-XC-27); a batch from before the upgrade on a
        // dataset it did not create does not know which ones it replaced, and keeps them all
        var owned = ShareBatchRollback.OwnedPaths(batch);
        if (ShareBatchRollback.ReplacedUnknown(batch))
        {
            _logger.LogWarning("The abandoned share batch {Token} of {User} on {OrgSlug}/{DsSlug} (started {Start:u}) " +
                               "is from before replaced files were recorded: it is closed, its {Files} file(s) stay",
                token, batch.UserName, orgSlug, dataset.Slug, batch.Start, batch.Entries.Count);
            return true;
        }

        _logger.LogWarning("Rolling back the abandoned share batch {Token} of {User} on {OrgSlug}/{DsSlug} (started {Start:u}, " +
                           "{Files} file(s), {Created})", token, batch.UserName, orgSlug, dataset.Slug, batch.Start,
            owned.Count, batch.CreatedDataset ? "it created the dataset" : "the dataset was there before");

        try
        {
            var ddb = _ddbManager.Get(orgSlug, internalRef);
            if (batch.CreatedDataset && ShareBatchRollback.HoldsOnly(ddb, owned))
                await _removal.RemoveAsSystemAsync(internalRef, $"abandoned share batch {token}", CancellationToken.None);
            else
            {
                if (batch.CreatedDataset)
                    _logger.LogWarning("The dataset {OrgSlug}/{DsSlug} that the abandoned batch {Token} created holds " +
                                       "entries added outside the batch: it is kept", orgSlug, dataset.Slug, token);
                await RemoveFilesAsync(ddb, orgSlug, dataset.Slug, internalRef, owned);
            }
        }
        catch
        {
            // Not rolled back: the batch is open again, the next run tries again
            await _db.Batches.Where(b => b.Token == token && b.Status == BatchStatus.Rolledback)
                .ExecuteUpdateAsync(set => set.SetProperty(b => b.Status, BatchStatus.Running)
                    .SetProperty(b => b.End, (DateTime?)null), CancellationToken.None);
            throw;
        }

        return true;
    }

    private async Task RemoveFilesAsync(IDDB ddb, string orgSlug, string dsSlug, Guid internalRef, List<string> paths)
    {
        var left = new List<string>();

        foreach (var path in paths)
        {
            if (ddb.EntryExists(path))
            {
                ddb.Remove(path);
                left.Add(path);
            }

            var local = ddb.GetLocalPath(path);
            if (File.Exists(local))
                File.Delete(local);
        }

        await _changes.EntriesLeftAsync(new DatasetKey(orgSlug, internalRef), left);
        _logger.LogInformation("Removed {Count} file(s) of an abandoned share batch from {OrgSlug}/{DsSlug}", left.Count,
            orgSlug, dsSlug);
    }
}
