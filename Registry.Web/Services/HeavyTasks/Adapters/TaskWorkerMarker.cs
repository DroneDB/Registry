using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// The mark of the worker that runs a heavy task (<c>JobIndex.WorkerServerId</c>): the folder of a deleted dataset is
/// deleted only when no worker of the dataset is still running (<see cref="DatasetCleanupService"/>).
/// <para>
/// The order is what makes it hold. The worker marks its row before it resolves the dataset, and clears the mark when it
/// has stopped; the delete of a dataset moves its rows to the tombstone slug before it queues the cleanup. So a worker
/// whose mark the cleanup did not see found no dataset and wrote nothing, and a worker whose mark it saw reads its row
/// after it cleared the mark, finds the tombstone and queues the cleanup again (<see cref="ClearAsync"/>).
/// </para>
/// </summary>
public sealed class TaskWorkerMarker
{
    /// <summary>The member of the Processing state that names the server of its worker.</summary>
    private const string ServerIdKey = "ServerId";

    private readonly RegistryContext _db;
    private readonly IBackgroundJobsProcessor _jobs;
    private readonly ILogger<TaskWorkerMarker> _log;

    public TaskWorkerMarker(RegistryContext db, IBackgroundJobsProcessor jobs, ILogger<TaskWorkerMarker> log)
    {
        _db = db;
        _jobs = jobs;
        _log = log;
    }

    /// <summary>The id of the server that runs the job of <paramref name="ctx"/> (its Processing state), or null when it cannot be read.</summary>
    public string? ServerIdOf(PerformContext ctx) =>
        BestEffort.Get<string?>(() =>
        {
            var state = ctx.Connection.GetStateData(ctx.BackgroundJob.Id);
            return state?.Name == ProcessingState.StateName && state.Data != null &&
                   state.Data.TryGetValue(ServerIdKey, out var serverId) && !string.IsNullOrEmpty(serverId)
                ? serverId
                : null;
        }, ex =>
        {
            _log.LogWarning(ex, "Cannot read the server that runs task {TaskId}", ctx.BackgroundJob.Id);
            return null;
        });

    /// <summary>Marks task <paramref name="jobId"/> as run by a worker of server <paramref name="serverId"/>.</summary>
    public Task SetAsync(string jobId, string serverId, CancellationToken ct) =>
        _db.JobIndices.Where(j => j.JobId == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.WorkerServerId, serverId), ct);

    /// <summary>
    /// Clears the mark of task <paramref name="jobId"/> once its worker has stopped; when its dataset was deleted
    /// meanwhile (its row has the tombstone slug, read after the mark is cleared) the cleanup of the dataset is queued
    /// again, since a cleanup that saw the mark left the folder. Never throws: the outcome of the task is its own.
    /// </summary>
    public Task ClearAsync(string jobId) =>
        BestEffort.RunAsync(async () =>
        {
            await _db.JobIndices.Where(j => j.JobId == jobId && j.WorkerServerId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.WorkerServerId, (string?)null), CancellationToken.None);

            var row = await _db.JobIndices.AsNoTracking().Where(j => j.JobId == jobId)
                .Select(j => new { j.OrgSlug, j.DsSlug }).FirstOrDefaultAsync(CancellationToken.None);
            if (row == null || !SlugKeyedRows.TryParseTombstone(row.DsSlug, out var internalRef))
                return;

            var cleanup = DatasetCleanupService.Queue(_jobs, row.OrgSlug, internalRef);
            _log.LogInformation("Task {TaskId} stopped after its dataset {OrgSlug}/{InternalRef} was deleted: " +
                                "cleanup job {CleanupJobId} deletes its folder", jobId, row.OrgSlug, internalRef, cleanup);
        }, ex => _log.LogError(ex, "Cannot clear the worker mark of task {TaskId}: the folder of its dataset, if it " +
                                   "was deleted, is left to the orphaned folder cleanup", jobId), CancellationToken.None);
}
