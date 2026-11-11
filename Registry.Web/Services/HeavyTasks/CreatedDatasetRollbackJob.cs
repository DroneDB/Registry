using System;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks;

/// <summary>
/// The delayed system delete of a dataset that a rolled-back task created (FR-XC-27, D-004, D-031), scheduled by
/// <see cref="TaskRollback"/>. It deletes the dataset only when it still holds nothing, and only while the task's record
/// still says it was rolled back: a task that runs again (a retry) keeps its dataset. The delete skips the access check
/// of a user (the system deletes it); the task and the user it ran for are logged.
/// </summary>
[AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 300])]
public sealed class CreatedDatasetRollbackJob
{
    private readonly ITaskWriteRecordStore _records;
    private readonly RegistryContext _db;
    private readonly IDdbManager _ddbManager;
    private readonly DatasetRemoval _removal;
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly ILogger<CreatedDatasetRollbackJob> _log;

    public CreatedDatasetRollbackJob(ITaskWriteRecordStore records, RegistryContext db, IDdbManager ddbManager,
        DatasetRemoval removal, IBackgroundJobsProcessor jobs, ILogger<CreatedDatasetRollbackJob> log)
    {
        _records = records;
        _db = db;
        _ddbManager = ddbManager;
        _removal = removal;
        _jobs = jobs;
        _log = log;
    }

    /// <summary>Deletes the dataset <paramref name="internalRef"/> that task <paramref name="taskId"/> created, if it still may.</summary>
    public async Task DeleteAsync(string taskId, Guid internalRef, string? userId)
    {
        // The task's row is gone with its history, or still says it was rolled back: a task that runs again keeps it
        var record = await _records.ReadAsync(taskId);
        if (record is not null &&
            (record.State is not (TaskWriteState.RollingBack or TaskWriteState.RolledBack) ||
             record.CreatedDataset?.InternalRef != internalRef))
        {
            _log.LogInformation("Task {TaskId} runs again or no longer holds the dataset {InternalRef}: it is kept",
                taskId, internalRef);
            return;
        }

        // Retried by the user before its rollback finished (its record stays rolling back until the new run starts,
        // which deletes what is left and keeps the dataset): queued or running again, it keeps its dataset. Both the
        // task's row and the live state of its job are read: the row follows the job a moment later
        var state = await _db.JobIndices.AsNoTracking().Where(j => j.JobId == taskId).Select(j => j.CurrentState)
            .FirstOrDefaultAsync();
        if (state == null || TaskStateCatalog.IsTerminal(state))
            state = JobRunsAgain(taskId);
        if (state != null && !TaskStateCatalog.IsTerminal(state))
        {
            _log.LogInformation("Task {TaskId} is {State} again: the dataset {InternalRef} it created is kept", taskId,
                state, internalRef);
            return;
        }

        var dataset = await _db.Datasets.AsNoTracking().Include(d => d.Organization)
            .FirstOrDefaultAsync(d => d.InternalRef == internalRef);
        if (dataset is null)
        {
            await DecidedAsync(taskId, internalRef);
            return;
        }

        if (HasEntries(_ddbManager.Get(dataset.Organization.Slug, internalRef)))
        {
            _log.LogWarning("The dataset {OrgSlug}/{DsSlug} ({InternalRef}) that task {TaskId} created holds entries " +
                            "added since: it is kept", dataset.Organization.Slug, dataset.Slug, internalRef, taskId);
            await DecidedAsync(taskId, internalRef);
            return;
        }

        await _removal.RemoveAsSystemAsync(internalRef,
            $"task {taskId} (user {userId ?? "(system)"}) created it and was rolled back");
        await DecidedAsync(taskId, internalRef);
    }

    // The delete was decided (the dataset went, or is kept for good): the startup sweep no longer schedules it again
    // (TaskWriteRecord.CreatedDatasetDeleteJob)
    private Task<TaskWriteRecordChange> DecidedAsync(string taskId, Guid internalRef) =>
        _records.ChangeAsync(taskId, r =>
        {
            if (r is not { CreatedDatasetDeleteJob: not null } || r.CreatedDataset?.InternalRef != internalRef)
                return null;

            r.CreatedDatasetDeleteJob = null;
            return r;
        });

    // The live state of the task's job when it waits or runs (a retry re-queued it), else null; a job the storage
    // cannot read is not running
    private string? JobRunsAgain(string taskId)
    {
        try
        {
            var status = _jobs.GetJobStatus(taskId);
            return status is JobStatus.Enqueued or JobStatus.Scheduled or JobStatus.Processing or JobStatus.Awaiting
                ? status.ToString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            _log.LogDebug(ex, "Cannot read the state of the job of task {TaskId}", taskId);
            return null;
        }
    }

    /// <summary>Whether anything is indexed in the dataset.</summary>
    internal static bool HasEntries(IDDB ddb) => ddb.FindSummaries(new EntryQuery(Recursive: false)) is { Count: > 0 };
}
