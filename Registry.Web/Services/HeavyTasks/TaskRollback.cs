using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// Default <see cref="ITaskRollback"/>. A recorded path is removed from the index, then its file; the recorded folders
/// go when they are empty, deepest first; the entries that left the dataset are announced
/// (<see cref="TaskEntryChanges"/>). A
/// dataset the task created is found by its folder id (never by its slug, which may have changed or been taken by
/// another dataset) and, when nothing else is indexed in it, deleted by the system a few minutes later
/// (<see cref="CreatedDatasetRollbackJob"/>), so that the task's error can still be read meanwhile.
/// </summary>
public sealed class TaskRollback : ITaskRollback
{
    /// <summary>How long after the rollback the system deletes the dataset the task created.</summary>
    public static readonly TimeSpan CreatedDatasetDeleteDelay = TimeSpan.FromMinutes(5);

    private const int BusyAttempts = 3;
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(1);

    private readonly ITaskWriteRecordStore _store;
    private readonly RegistryContext _db;
    private readonly IDdbManager _ddbManager;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly TaskEntryChanges _changes;
    private readonly ILogger<TaskRollback> _log;

    public TaskRollback(ITaskWriteRecordStore store, RegistryContext db, IDdbManager ddbManager,
        IBackgroundJobsProcessor processor, TaskEntryChanges changes, ILogger<TaskRollback> log)
    {
        _store = store;
        _db = db;
        _ddbManager = ddbManager;
        _processor = processor;
        _log = log;
        _changes = changes;
    }

    public async Task<TaskRollbackOutcome> RollBackAsync(string taskId, IReadOnlyCollection<PendingWrite>? pending = null,
        CancellationToken ct = default)
    {
        // What the worker had not written to the record yet goes into it first: the reconciliation sweep leaves it
        // alone (HeldPathsAsync), and a rollback that a stop interrupts still knows it
        if (pending is { Count: > 0 })
            await _store.ChangeAsync(taskId, r =>
            {
                if (r is not { State: TaskWriteState.Open or TaskWriteState.CancelRequested })
                    return null;

                AddPending(r, pending);
                return r;
            }, ct);

        var claimed = await _store.ClaimRollbackAsync(taskId, ct);
        if (claimed is null)
        {
            var current = await _store.ReadAsync(taskId, ct);
            if (current?.State == TaskWriteState.Committed)
            {
                _log.LogInformation("Task {TaskId} is committed: it is not rolled back", taskId);
                return TaskRollbackOutcome.Committed;
            }

            // An import that ran at the upgrade has no record (§8.3): its dataset is the one the import created
            if (current is null)
                claimed = await ClaimPreUpgradeImportAsync(taskId, ct);
        }

        if (claimed is null)
        {
            // No record, or rolled back by another path (a cancel, the sweep): only what was not written to it is left
            if (pending is not { Count: > 0 })
                return TaskRollbackOutcome.Nothing;

            var unrecorded = new TaskWriteRecord();
            AddPending(unrecorded, pending);
            return await DeleteWritesAsync(taskId, unrecorded.Datasets, ct)
                ? TaskRollbackOutcome.RolledBack
                : TaskRollbackOutcome.Incomplete;
        }

        if (pending is { Count: > 0 })
            AddPending(claimed, pending);

        _log.LogWarning("Rolling back task {TaskId}: {Paths} path(s) and {Folders} folder(s) it created{Dataset}", taskId,
            claimed.Datasets.Sum(d => d.Paths.Count), claimed.Datasets.Sum(d => d.Folders.Count),
            claimed.CreatedDataset is { } c ? $", and the dataset {c.Org}/{c.Slug} ({c.InternalRef})" : "");

        var complete = await DeleteWritesAsync(taskId, claimed.Datasets, ct);
        if (claimed.CreatedDataset is { } created)
            await ScheduleCreatedDatasetDeleteAsync(taskId, created, claimed.CreatedDatasetDeleteJob, ct);

        if (!complete)
        {
            _log.LogWarning("The rollback of task {TaskId} could not delete everything it recorded: the startup sweep " +
                            "tries again", taskId);
            return TaskRollbackOutcome.Incomplete;
        }

        await _store.MarkRolledBackAsync(taskId, ct);
        return TaskRollbackOutcome.RolledBack;
    }

    public async Task<TaskAttemptStart> StartAttemptAsync(string taskId, int retryCount, CancellationToken ct = default)
    {
        var record = await _store.ReadAsync(taskId, ct);
        if (record is null)
        {
            // An import queued before the upgrade has no record (§8.3): it starts one with the dataset the import created
            var created = await PreUpgradeImportDatasetAsync(taskId, ct);
            await _store.ChangeAsync(taskId,
                r => r is null ? new TaskWriteRecord { Attempt = retryCount, CreatedDataset = created } : null, ct);
            return TaskAttemptStart.Fresh;
        }

        var sameAttempt = record.Attempt >= retryCount;

        // A committed task never runs again (FR-XC-27): lost by a restart, or failed after its commit and retried by
        // the retry policy (its sources may be gone already)
        if (record.State == TaskWriteState.Committed)
            return TaskAttemptStart.CommittedLost;

        // A rollback that a stop interrupted (or finished just before the job was run again), or the writes of a run
        // that a restart lost: deleted before this attempt writes anything
        if (record.State is TaskWriteState.RollingBack or TaskWriteState.RolledBack || (sameAttempt && record.HasWrites))
        {
            await RollBackLostRunAsync(taskId, record.State, retryCount, ct);
            return TaskAttemptStart.LostRunRolledBack;
        }

        await SetAttemptAsync(taskId, retryCount, ct);
        return record.HasWrites ? TaskAttemptStart.Retry : TaskAttemptStart.Fresh;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        // First the delayed delete of a created dataset that the job storage lost (InMemory storage: a restart): it is
        // scheduled again, and the job decides again whether the dataset still may go. Before the rollbacks below,
        // which schedule their own
        var rescheduled = 0;
        foreach (var taskId in await _store.EndedWithCreatedDatasetDeleteAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RescheduleLostCreatedDatasetDeleteAsync(taskId, ct))
                    rescheduled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Cannot schedule again the delete of the dataset task {TaskId} created", taskId);
            }
        }

        if (rescheduled > 0)
            _log.LogWarning("Scheduled again the delete of {Count} dataset(s) created by rolled-back tasks, whose job " +
                            "was lost", rescheduled);

        var rolledBack = 0;
        foreach (var taskId in await _store.EndedWithOpenRecordAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RollBackAsync(taskId, null, ct) == TaskRollbackOutcome.RolledBack)
                    rolledBack++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Cannot roll back task {TaskId}", taskId);
            }
        }

        if (rolledBack > 0)
            _log.LogWarning("Rolled back {Count} ended task(s) whose rollback had not finished", rolledBack);
        return rolledBack;
    }

    // The delete job the record names is gone (or ended without deciding): scheduled again. A job still waiting stays
    private async Task<bool> RescheduleLostCreatedDatasetDeleteAsync(string taskId, CancellationToken ct)
    {
        var record = await _store.ReadAsync(taskId, ct);
        if (record is not { State: TaskWriteState.RolledBack, CreatedDataset: { } created, CreatedDatasetDeleteJob: { } job })
            return false;

        if (JobWaits(job))
            return false;

        _log.LogWarning("The delete job {JobId} of the dataset {InternalRef} that task {TaskId} created is gone: it is " +
                        "scheduled again", job, created.InternalRef, taskId);
        return await ScheduleCreatedDatasetDeleteAsync(taskId, created, null, ct) != null;
    }

    // Whether the job is still to run (or running); a job the storage does not have (or cannot read) is not
    private bool JobWaits(string jobId)
    {
        try
        {
            return _processor.GetJobStatus(jobId) is JobStatus.Scheduled or JobStatus.Enqueued or JobStatus.Processing
                or JobStatus.Awaiting;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            _log.LogDebug(ex, "Cannot read the state of job {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// The dataset of an import task that has no write record (NULL: queued or running at the upgrade, §8.3): every
    /// such task was submitted by <c>ImportManager.CreateAsync</c>, which created the dataset it fills, so a rollback
    /// of it deletes that dataset, and only when nothing is indexed in it (<see cref="CreatedDatasetRollbackJob"/>).
    /// Since the upgrade a task of a tool that adds dataset files is queued with a record. Null for any other task.
    /// </summary>
    private async Task<TaskCreatedDataset?> PreUpgradeImportDatasetAsync(string taskId, CancellationToken ct)
    {
        var row = await _db.JobIndices.AsNoTracking()
            .Where(j => j.JobId == taskId)
            .Select(j => new { j.ToolId, j.OrgSlug, j.DsSlug, j.WriteRecordJson })
            .FirstOrDefaultAsync(ct);
        if (row is not { WriteRecordJson: null, ToolId: HeavyToolIds.ImportDataset, OrgSlug: { } org, DsSlug: { } slug })
            return null;

        var dataset = await _db.Datasets.AsNoTracking().Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.Slug == slug && d.Organization.Slug == org, ct);
        if (dataset is null)
            return null;

        _log.LogWarning("Import task {TaskId} has no write record (queued before the upgrade): the dataset {Org}/{Slug} " +
                        "({InternalRef}) it imports into is the one it created", taskId, org, slug, dataset.InternalRef);
        return new TaskCreatedDataset(org, dataset.InternalRef, slug);
    }

    // The record of an import from before the upgrade, claimed for its rollback: only its created dataset is in it
    private async Task<TaskWriteRecord?> ClaimPreUpgradeImportAsync(string taskId, CancellationToken ct)
    {
        if (await PreUpgradeImportDatasetAsync(taskId, ct) is not { } created)
            return null;

        var change = await _store.ChangeAsync(taskId, r => r is null
            ? new TaskWriteRecord { State = TaskWriteState.RollingBack, CreatedDataset = created }
            : null, ct);
        return change.Changed ? change.Record : null;
    }

    // The lost run's writes go; the dataset the task created stays (the job runs again into it), and so does a cancel
    // request. What cannot be deleted stays recorded, for the rollback of the task when it ends
    private async Task RollBackLostRunAsync(string taskId, TaskWriteState stateBefore, int retryCount,
        CancellationToken ct)
    {
        var claimed = await _store.ClaimRollbackAsync(taskId, ct);
        var complete = true;
        if (claimed != null)
        {
            _log.LogWarning("Task {TaskId} runs again after a run that was lost: deleting the {Paths} path(s) it created",
                taskId, claimed.Datasets.Sum(d => d.Paths.Count));
            complete = await DeleteWritesAsync(taskId, claimed.Datasets, ct);
        }

        // A cancel that came while the cleanup held the record (RequestCancelAsync keeps it as CancelPending) is kept:
        // the task can no longer commit
        await _store.ChangeAsync(taskId, r => r is null
            ? null
            : new TaskWriteRecord
            {
                State = stateBefore == TaskWriteState.CancelRequested || r.State == TaskWriteState.CancelRequested ||
                        r.CancelPending
                    ? TaskWriteState.CancelRequested
                    : TaskWriteState.Open,
                Attempt = retryCount,
                CreatedDataset = r.CreatedDataset,
                Datasets = complete ? [] : r.Datasets
            }, ct);
    }

    public async Task<TaskCommittedLostException?> CommittedLostAsync(string taskId, CancellationToken ct = default)
    {
        var record = await _store.ReadAsync(taskId, ct);
        if (record is not { State: TaskWriteState.Committed })
            return null;
        if (record.CommittedSources is not { Paths.Count: > 0 } sources)
            return new TaskCommittedLostException();

        // By its folder id: the slug may have changed since. A dataset that is gone holds none of its sources
        var dataset = await _db.Datasets.AsNoTracking().Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.InternalRef == sources.InternalRef, ct);
        if (dataset is null)
            return TaskCommittedLostException.For(sources.Paths, _ => false);

        var ddb = _ddbManager.Get(dataset.Organization.Slug, sources.InternalRef);
        return TaskCommittedLostException.For(sources.Paths, path =>
        {
            try
            {
                return File.Exists(ddb.GetLocalPath(path));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Cannot tell whether source '{Path}' of task {TaskId} remains", path, taskId);
                return true;
            }
        });
    }

    private Task<TaskWriteRecordChange> SetAttemptAsync(string taskId, int retryCount, CancellationToken ct) =>
        _store.ChangeAsync(taskId, r =>
        {
            if (r is null || r.Attempt == retryCount)
                return null;
            r.Attempt = retryCount;
            return r;
        }, ct);

    // The paths, folders and stamps the worker had not written to the record (a stamp keeps a file somebody replaced)
    private static void AddPending(TaskWriteRecord record, IEnumerable<PendingWrite> pending)
    {
        foreach (var group in pending.GroupBy(p => p.Dataset))
            record.Add(group.Key, group.Where(p => !p.IsFolder && p.Stamp is null).Select(p => p.Path),
                group.Where(p => p.IsFolder).Select(p => p.Path),
                group.Where(p => !p.IsFolder && p.Stamp is not null)
                    .GroupBy(p => p.Path).Select(g => KeyValuePair.Create(g.Key, g.Last().Stamp!)));
    }

    /// <summary>Deletes the recorded paths, then the recorded folders that are empty; false when something could not go.</summary>
    private async Task<bool> DeleteWritesAsync(string taskId, IEnumerable<TaskWrittenDataset> datasets,
        CancellationToken ct)
    {
        var complete = true;
        foreach (var written in datasets.Where(d => d.Paths.Count > 0 || d.Folders.Count > 0))
        {
            ct.ThrowIfCancellationRequested();

            // By its folder id: the slug may have changed since
            var dataset = await _db.Datasets.AsNoTracking().Include(d => d.Organization)
                .FirstOrDefaultAsync(d => d.InternalRef == written.InternalRef, ct);
            if (dataset is null)
            {
                _log.LogInformation("The dataset {InternalRef} that task {TaskId} wrote is gone: nothing to delete there",
                    written.InternalRef, taskId);
                continue;
            }

            var orgSlug = dataset.Organization.Slug;
            var ddb = _ddbManager.Get(orgSlug, written.InternalRef);
            var left = new List<string>();

            foreach (var path in written.Paths.Distinct(StringComparer.Ordinal))
                complete &= await DeletePathAsync(taskId, ddb, path, written.Stamps?.GetValueOrDefault(path), left, ct);

            // Deepest first: a folder's recorded subfolders go before it
            foreach (var folder in written.Folders.Distinct(StringComparer.Ordinal)
                         .OrderByDescending(f => f.Count(ch => ch is '/' or '\\')).ThenByDescending(f => f.Length))
                complete &= await DeleteFolderIfEmptyAsync(taskId, ddb, folder, left, ct);

            if (left.Count > 0)
                await _changes.EntriesLeftAsync(orgSlug, dataset.Slug, left);
        }

        return complete;
    }

    private async Task<bool> DeletePathAsync(string taskId, IDDB ddb, string path, string? stamp, List<string> left,
        CancellationToken ct)
    {
        try
        {
            // Written by the task, then replaced by somebody else (an upload with overwrite): it is theirs now
            if (stamp != null && TaskWriteRecord.StampOf(ddb.GetLocalPath(path)) is { } now && now != stamp)
            {
                _log.LogWarning("The rollback of task {TaskId} keeps '{Path}': it changed since the task wrote it",
                    taskId, path);
                return true;
            }

            if (await WhenNotBusyAsync(() => ddb.EntryExists(path), ct))
            {
                await WhenNotBusyAsync(() =>
                {
                    ddb.Remove(path);
                    return true;
                }, ct);
                left.Add(path);
            }

            var local = ddb.GetLocalPath(path);
            if (File.Exists(local))
                File.Delete(local);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "The rollback of task {TaskId} cannot delete '{Path}'", taskId, path);
            return false;
        }
    }

    private async Task<bool> DeleteFolderIfEmptyAsync(string taskId, IDDB ddb, string folder, List<string> left,
        CancellationToken ct)
    {
        try
        {
            var local = ddb.GetLocalPath(folder);
            // A folder that holds anything else (a file another writer put there) stays: it is not only the task's
            if (!Directory.Exists(local) || Directory.EnumerateFileSystemEntries(local).Any())
                return true;

            var indexed = await WhenNotBusyAsync(() => ddb.EntryExists(folder), ct);
            if (indexed && await WhenNotBusyAsync(
                    () => ddb.FindSummaries(EntryQuery.Under(folder)).All(e => e.Type == EntryType.Directory), ct))
            {
                await WhenNotBusyAsync(() =>
                {
                    ddb.Remove(folder);
                    return true;
                }, ct);
                left.Add(folder);
            }

            Directory.Delete(local, false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "The rollback of task {TaskId} cannot delete the folder '{Folder}'", taskId, folder);
            return false;
        }
    }

    // The index of a dataset may be busy for a moment (another writer): a few short waits before the rollback gives up
    private static async Task<T> WhenNotBusyAsync<T>(Func<T> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (DdbBusyException) when (attempt < BusyAttempts)
            {
                await Task.Delay(BusyDelay, ct);
            }
        }
    }

    /// <summary>
    /// Schedules the delayed delete of the dataset the task created, when it is still there and holds nothing, and
    /// names the job in the record (until the job decides: <see cref="TaskWriteRecord.CreatedDatasetDeleteJob"/>), so
    /// that a startup sweep schedules it again if the job storage loses it. A job of an earlier rollback still waiting
    /// (<paramref name="existingJob"/>) is kept. The job named in the record; null when none is due.
    /// </summary>
    private async Task<string?> ScheduleCreatedDatasetDeleteAsync(string taskId, TaskCreatedDataset created,
        string? existingJob, CancellationToken ct)
    {
        if (existingJob != null && JobWaits(existingJob))
        {
            _log.LogInformation("The delete of the dataset {InternalRef} that task {TaskId} created is scheduled already " +
                                "(job {JobId})", created.InternalRef, taskId, existingJob);
            return existingJob;
        }

        var dataset = await _db.Datasets.AsNoTracking().Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.InternalRef == created.InternalRef, ct);
        if (dataset is null)
        {
            _log.LogInformation("The dataset {Org}/{Slug} ({InternalRef}) task {TaskId} created is gone already",
                created.Org, created.Slug, created.InternalRef, taskId);
            await NameDeleteJobAsync(taskId, created, null, ct);
            return null;
        }

        if (CreatedDatasetRollbackJob.HasEntries(_ddbManager.Get(dataset.Organization.Slug, created.InternalRef)))
        {
            _log.LogWarning("The dataset {Org}/{Slug} ({InternalRef}) task {TaskId} created holds other entries: it is " +
                            "kept", dataset.Organization.Slug, dataset.Slug, created.InternalRef, taskId);
            await NameDeleteJobAsync(taskId, created, null, ct);
            return null;
        }

        var userId = await _db.JobIndices.AsNoTracking().Where(j => j.JobId == taskId).Select(j => j.UserId)
            .FirstOrDefaultAsync(ct);
        var jobId = _processor.Schedule<CreatedDatasetRollbackJob>(
            job => job.DeleteAsync(taskId, created.InternalRef, userId), CreatedDatasetDeleteDelay);
        _log.LogWarning("The dataset {Org}/{Slug} ({InternalRef}) that task {TaskId} of user {UserId} created is deleted " +
                        "in {Delay} (job {JobId})", dataset.Organization.Slug, dataset.Slug, created.InternalRef, taskId,
            userId ?? "(system)", CreatedDatasetDeleteDelay, jobId);
        await NameDeleteJobAsync(taskId, created, jobId, ct);
        return jobId;
    }

    // The delete job of the created dataset in the record (null: none is due); a record that no longer holds the
    // dataset (the task runs again) is left alone
    private Task<TaskWriteRecordChange> NameDeleteJobAsync(string taskId, TaskCreatedDataset created, string? jobId,
        CancellationToken ct) =>
        _store.ChangeAsync(taskId, r =>
        {
            if (r is not { State: TaskWriteState.RollingBack or TaskWriteState.RolledBack } ||
                r.CreatedDataset?.InternalRef != created.InternalRef || r.CreatedDatasetDeleteJob == jobId)
                return null;

            r.CreatedDatasetDeleteJob = jobId;
            return r;
        }, ct);
}
