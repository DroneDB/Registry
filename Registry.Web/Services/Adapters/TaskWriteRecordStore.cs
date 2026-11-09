using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Services.Caching;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="ITaskWriteRecordStore"/>: compare-and-swap updates of <c>JobIndex.WriteRecordJson</c> (the whole
/// string, which both SQLite and MySQL <c>longtext</c> compare). A change of the record's state renews the dataset's
/// task list, whose rows say whether a task can still be cancelled.
/// </summary>
public sealed class TaskWriteRecordStore(RegistryContext db, IRecordCache cache, ILogger<TaskWriteRecordStore> log,
    ITaskChangeNotifier? notifier = null) : ITaskWriteRecordStore
{
    /// <summary>How many times a change is tried again after another writer changed the record first.</summary>
    internal const int MaxAttempts = 20;

    // What a record that still needs its rollback starts with (the state is the first member TaskWriteRecord writes)
    private const string OpenPrefix = "{\"state\":\"open\"";
    private const string CancelRequestedPrefix = "{\"state\":\"cancelRequested\"";
    private const string RollingBackPrefix = "{\"state\":\"rollingBack\"";
    private const string RolledBackPrefix = "{\"state\":\"rolledBack\"";
    private const string CreatedDatasetDeleteJobMember = "\"createdDatasetDeleteJob\"";

    public async Task<TaskWriteRecord?> ReadAsync(string taskId, CancellationToken ct = default) =>
        TaskWriteRecord.Parse((await ReadRowAsync(taskId, ct))?.WriteRecordJson);

    public async Task<TaskWriteRecordChange> ChangeAsync(string taskId, Func<TaskWriteRecord?, TaskWriteRecord?> change,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        for (var attempt = 1; ; attempt++)
        {
            var row = await ReadRowAsync(taskId, ct);
            if (row is null)
                return new TaskWriteRecordChange(null, false, false);

            var current = TaskWriteRecord.Parse(row.WriteRecordJson);
            // A fresh copy: the change may modify what it is given
            var next = change(TaskWriteRecord.Parse(row.WriteRecordJson));
            if (next is null)
                return new TaskWriteRecordChange(current, false, true);

            var json = next.ToJson();
            if (json == row.WriteRecordJson)
                return new TaskWriteRecordChange(next, false, true);

            var old = row.WriteRecordJson;
            var updated = old is null
                ? await db.JobIndices.Where(j => j.JobId == taskId && j.WriteRecordJson == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.WriteRecordJson, json), ct)
                : await db.JobIndices.Where(j => j.JobId == taskId && j.WriteRecordJson == old)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.WriteRecordJson, json), ct);

            // A set-based update bypasses the change tracker: drop any stale tracked copy
            var local = db.JobIndices.Local.FirstOrDefault(j => j.JobId == taskId);
            if (local != null)
                db.Entry(local).State = EntityState.Detached;

            if (updated > 0)
            {
                if (current?.State != next.State && row.OrgSlug != null && row.DsSlug != null)
                    await InvalidateTasksListAsync(row.OrgSlug, row.DsSlug, ct);
                return new TaskWriteRecordChange(next, true, true);
            }

            if (attempt >= MaxAttempts)
                throw new InvalidOperationException($"The write record of task {taskId} kept changing");

            log.LogDebug("The write record of task {TaskId} changed concurrently; reading it again", taskId);
        }
    }

    public async Task RecordAsync(string taskId, DatasetKey dataset, IReadOnlyCollection<string> paths,
        IReadOnlyCollection<string> folders, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? stamps = null)
    {
        if (paths.Count == 0 && folders.Count == 0 && stamps is not { Count: > 0 })
            return;

        var closed = false;
        await ChangeAsync(taskId, record =>
        {
            record ??= new TaskWriteRecord();
            if (record.State is TaskWriteState.RollingBack or TaskWriteState.RolledBack)
            {
                closed = true;
                return null;
            }

            closed = false;
            record.Add(dataset, paths, folders, stamps);
            return record;
        }, ct);

        if (closed)
            throw TaskWriteRecordClosedException.RollingBack(taskId);
    }

    public async Task SetCreatedDatasetAsync(string taskId, TaskCreatedDataset created, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(created);

        var closed = false;
        await ChangeAsync(taskId, record =>
        {
            record ??= new TaskWriteRecord();
            closed = record.State is TaskWriteState.RollingBack or TaskWriteState.RolledBack;
            if (closed)
                return null;

            record.CreatedDataset = created;
            return record;
        }, ct);

        if (closed)
            throw TaskWriteRecordClosedException.RollingBack(taskId);
    }

    public async Task<bool> MarkCommittedAsync(string taskId, CancellationToken ct = default,
        TaskCommittedSources? sources = null)
    {
        var result = await ChangeAsync(taskId, record =>
        {
            record ??= new TaskWriteRecord();
            if (record.State != TaskWriteState.Open)
                return null;

            record.State = TaskWriteState.Committed;
            if (sources is { Paths.Count: > 0 })
                record.CommittedSources = sources;
            return record;
        }, ct);

        return result.Record?.State == TaskWriteState.Committed;
    }

    public async Task<TaskCancelRequestResult> RequestCancelAsync(string taskId, CancellationToken ct = default)
    {
        var result = await ChangeAsync(taskId, record =>
        {
            switch (record)
            {
                case { State: TaskWriteState.Open }:
                    record.State = TaskWriteState.CancelRequested;
                    return record;
                // A rollback holds it: the cleanup of a lost run before the task runs again writes the record back,
                // as cancelRequested (TaskRollback.StartAttemptAsync); the rollback of an ended task ignores it
                case { State: TaskWriteState.RollingBack, CancelPending: false }:
                    record.CancelPending = true;
                    return record;
                default:
                    return null;
            }
        }, ct);

        return result.Record?.State switch
        {
            null => TaskCancelRequestResult.NoRecord,
            TaskWriteState.Committed => TaskCancelRequestResult.Committed,
            TaskWriteState.RollingBack => TaskCancelRequestResult.Requested,
            TaskWriteState.RolledBack => TaskCancelRequestResult.RolledBack,
            _ => TaskCancelRequestResult.Requested
        };
    }

    public async Task<TaskWriteRecord?> ClaimRollbackAsync(string taskId, CancellationToken ct = default)
    {
        // Each claim writes a new claim id, so a claim always changes the record: a compare-and-swap that wrote nothing
        // is no claim. A record another rollback claimed while this one tried (its claim differs from the one first
        // read) is that rollback's
        var claim = Guid.NewGuid().ToString("N");
        var firstRead = true;
        string? claimRead = null;
        var result = await ChangeAsync(taskId, record =>
        {
            if (firstRead)
            {
                firstRead = false;
                claimRead = record?.Claim;
            }
            else if (record is { State: TaskWriteState.RollingBack } && record.Claim != claimRead)
                return null;

            if (record is not { NeedsRollback: true })
                return null;

            record.State = TaskWriteState.RollingBack;
            record.Claim = claim;
            return record;
        }, ct);

        return result is { Changed: true, Record: { State: TaskWriteState.RollingBack } claimed } ? claimed : null;
    }

    public Task MarkRolledBackAsync(string taskId, CancellationToken ct = default) =>
        ChangeAsync(taskId, record =>
        {
            if (record is not { State: TaskWriteState.RollingBack })
                return null;

            record.State = TaskWriteState.RolledBack;
            return record;
        }, ct);

    public Task ForgetAsync(string taskId, DatasetKey dataset, string path, CancellationToken ct = default) =>
        ChangeAsync(taskId, record =>
        {
            var written = record?.Datasets.FirstOrDefault(d => d.InternalRef == dataset.InternalRef &&
                                                               d.Org == dataset.OrgSlug);
            return written != null && written.Paths.Remove(path) ? record : null;
        }, ct);

    public async Task ClearAsync(string taskId, CancellationToken ct = default)
    {
        await db.JobIndices.Where(j => j.JobId == taskId && j.WriteRecordJson != null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.WriteRecordJson, (string?)null), ct);

        var local = db.JobIndices.Local.FirstOrDefault(j => j.JobId == taskId);
        if (local != null)
            db.Entry(local).State = EntityState.Detached;
    }

    public async Task<IReadOnlyList<string>> EndedWithOpenRecordAsync(CancellationToken ct = default) =>
        await Unfinished(db.JobIndices.AsNoTracking()
                .Where(j => j.CurrentState == FailedState.StateName || j.CurrentState == DeletedState.StateName))
            .OrderBy(j => j.CreatedAtUtc)
            .Select(j => j.JobId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> EndedWithCreatedDatasetDeleteAsync(CancellationToken ct = default)
    {
        var rows = await db.JobIndices.AsNoTracking()
            .Where(j => j.CurrentState == FailedState.StateName || j.CurrentState == DeletedState.StateName)
            .Where(j => j.WriteRecordJson != null && j.WriteRecordJson.StartsWith(RolledBackPrefix) &&
                        j.WriteRecordJson.Contains(CreatedDatasetDeleteJobMember))
            .OrderBy(j => j.CreatedAtUtc)
            .Select(j => new { j.JobId, j.WriteRecordJson })
            .ToListAsync(ct);

        // The text test may also match a path of that name: the record says
        return [.. rows.Where(r => TaskWriteRecord.Parse(r.WriteRecordJson)?.CreatedDatasetDeleteJob != null)
            .Select(r => r.JobId)];
    }

    public async Task<IReadOnlySet<string>> HeldPathsAsync(DatasetKey dataset, CancellationToken ct = default)
    {
        var reference = dataset.InternalRef.ToString();
        var records = await Unfinished(db.JobIndices.AsNoTracking()
                .Where(j => j.CurrentState != SucceededState.StateName))
            .Where(j => j.WriteRecordJson!.Contains(reference))
            .Select(j => j.WriteRecordJson)
            .ToListAsync(ct);

        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.Select(TaskWriteRecord.Parse))
        {
            if (record is not { NeedsRollback: true })
                continue;

            foreach (var written in record.Datasets.Where(d => d.InternalRef == dataset.InternalRef &&
                                                               d.Org == dataset.OrgSlug))
                held.UnionWith(written.Paths);
        }

        return held;
    }

    // The rows whose record still needs its rollback (open, cancelRequested, rollingBack), told from the first bytes
    private static IQueryable<Data.Models.JobIndex> Unfinished(IQueryable<Data.Models.JobIndex> rows) =>
        rows.Where(j => j.WriteRecordJson != null &&
                        (j.WriteRecordJson.StartsWith(OpenPrefix) ||
                         j.WriteRecordJson.StartsWith(CancelRequestedPrefix) ||
                         j.WriteRecordJson.StartsWith(RollingBackPrefix)));

    private sealed record RecordRow(string? WriteRecordJson, string? OrgSlug, string? DsSlug);

    private Task<RecordRow?> ReadRowAsync(string taskId, CancellationToken ct) =>
        db.JobIndices.AsNoTracking()
            .Where(j => j.JobId == taskId)
            .Select(j => new RecordRow(j.WriteRecordJson, j.OrgSlug, j.DsSlug))
            .FirstOrDefaultAsync(ct);

    private async Task InvalidateTasksListAsync(string orgSlug, string dsSlug, CancellationToken ct)
    {
        // Logs and swallows a cache error itself
        await cache.RemoveAsync(CacheCategories.TasksList(orgSlug, dsSlug), ct);
        notifier?.Publish(orgSlug, dsSlug);
    }
}
