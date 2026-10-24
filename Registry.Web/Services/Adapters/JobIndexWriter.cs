using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Adapters;

#nullable enable

/// <summary>
/// Write operations on the JobIndex table for Hangfire job tracking (upsert, update state, delete).
/// Also invalidates the per-dataset task-list cache after each mutation.
/// </summary>
public class JobIndexWriter(RegistryContext db, ILogger<JobIndexWriter> log,
    IRecordCache cache, ITaskChangeNotifier? notifier = null) : IJobIndexWriter
{
    public async Task UpsertOnEnqueueAsync(string jobId, IndexPayload meta, DateTime createdAtUtc,
        string? methodDisplay, CancellationToken ct = default)
    {
        meta.EnsureValid();

        var existing = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (existing is null)
        {
            db.JobIndices.Add(new JobIndex
            {
                JobId = jobId,
                OrgSlug = meta.OrgSlug,
                DsSlug = meta.DsSlug,
                Hash = meta.Hash,
                Path = meta.Path,
                UserId = meta.UserId,
                Queue = meta.Queue,
                CreatedAtUtc = createdAtUtc,
                LastStateChangeUtc = createdAtUtc,
                CurrentState = "Created",
                MethodDisplay = methodDisplay,
                ToolId = string.IsNullOrWhiteSpace(meta.ToolId) ? "build" : meta.ToolId,
                ToolVersion = string.IsNullOrWhiteSpace(meta.ToolVersion) ? "1" : meta.ToolVersion,
                RequestHash = meta.RequestHash,
                ParentJobId = meta.ParentJobId,
                WorkflowExecutionId = meta.WorkflowExecutionId
            });
        }
        else
        {
            existing.OrgSlug = meta.OrgSlug;
            existing.DsSlug = meta.DsSlug;
            existing.Hash = meta.Hash;
            existing.Path = meta.Path;
            existing.UserId = meta.UserId;
            existing.Queue = meta.Queue ?? existing.Queue;
            existing.MethodDisplay = methodDisplay ?? existing.MethodDisplay;

            existing.ToolId = string.IsNullOrWhiteSpace(meta.ToolId) ? existing.ToolId : meta.ToolId;
            existing.ToolVersion = string.IsNullOrWhiteSpace(meta.ToolVersion) ? existing.ToolVersion : meta.ToolVersion;
            existing.RequestHash = meta.RequestHash;
            existing.ParentJobId = meta.ParentJobId;
            existing.WorkflowExecutionId = meta.WorkflowExecutionId;

            // Reset to new creation time when re-enqueueing
            existing.CreatedAtUtc = createdAtUtc;
            existing.LastStateChangeUtc = createdAtUtc;
            existing.CurrentState = "Created";

            // CRITICAL: Reset all state timestamps when re-enqueueing a job
            // This ensures that old timestamps from previous runs don't persist
            existing.ProcessingAtUtc = null;
            existing.SucceededAtUtc = null;
            existing.FailedAtUtc = null;
            existing.DeletedAtUtc = null;
            existing.ScheduledAtUtc = null;

            // Reset progress/artifact telemetry on re-enqueue
            existing.ProgressPercent = null;
            existing.PhaseMessage = null;
            existing.ArtifactSizeBytes = null;
            existing.ArtifactSha256 = null;
            existing.ErrorType = null;
            existing.LogTailJson = null;
            existing.ProgressUpdatedAtUtc = null;
        }

        await db.SaveChangesAsync(ct);

        // Invalidate task-list cache for the affected dataset
        await InvalidateTasksListCacheAsync(meta.OrgSlug, meta.DsSlug, ct);
    }

    private const string IndexingStateName = "Indexing";
    private const int MaxPreparationAttempts = 3;

    public async Task<JobIndexPreparationResult> PrepareForPublicationAsync(string jobId, IndexPayload meta,
        DateTime createdAtUtc, string? methodDisplay, CancellationToken ct = default)
    {
        meta.EnsureValid();

        // Publication records carry millisecond precision, which both MySQL datetime(6) and SQLite
        // TEXT columns round-trip exactly, so "same creation time" means "same publication".
        var created = new DateTime(createdAtUtc.Ticks - createdAtUtc.Ticks % TimeSpan.TicksPerMillisecond,
            DateTimeKind.Utc);
        var toolId = string.IsNullOrWhiteSpace(meta.ToolId) ? "build" : meta.ToolId;

        for (var attempt = 1; ; attempt++)
        {
            var existing = await db.JobIndices.AsNoTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
            if (existing is null)
            {
                var row = new JobIndex { JobId = jobId };
                ApplyFreshIndexingRow(row, meta, created, methodDisplay);
                db.JobIndices.Add(row);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    // The context is scoped and reused (e.g. by the recovery pass): never leave the
                    // failed insert tracked, or the next SaveChanges would replay it.
                    db.Entry(row).State = EntityState.Detached;
                    if (ex is not DbUpdateException || attempt >= MaxPreparationAttempts)
                        throw;

                    // A concurrent preparer inserted the row first: classify against its row instead.
                    log.LogDebug(ex, "JobIndex row of job {JobId} was inserted concurrently; re-reading", jobId);
                    continue;
                }

                await InvalidateTasksListCacheAsync(meta.OrgSlug, meta.DsSlug, ct);
                return JobIndexPreparationResult.Inserted;
            }

            // The row of this publication is never modified, whatever state it reached.
            if (existing.CreatedAtUtc.Ticks == created.Ticks)
                return existing.OrgSlug == meta.OrgSlug && existing.DsSlug == meta.DsSlug &&
                       existing.ToolId == toolId && existing.RequestHash == meta.RequestHash
                    ? JobIndexPreparationResult.Compatible
                    : JobIndexPreparationResult.Incompatible;

            // A different creation time means the job id was reused (e.g. Hangfire in-memory storage
            // restarts its ids after a process restart) and the row belongs to an earlier job.
            if (await TryReplaceWithIndexingRowAsync(jobId, meta, created, methodDisplay, ct))
            {
                log.LogWarning("Replaced JobIndex row of job {JobId} created at {PreviousCreatedAtUtc} " +
                               "(state {PreviousState}) left over from an earlier job with the same id",
                    jobId, existing.CreatedAtUtc, existing.CurrentState);

                if (existing.OrgSlug != meta.OrgSlug || existing.DsSlug != meta.DsSlug)
                    await InvalidateTasksListCacheAsync(existing.OrgSlug, existing.DsSlug, ct);
                await InvalidateTasksListCacheAsync(meta.OrgSlug, meta.DsSlug, ct);
                return JobIndexPreparationResult.Replaced;
            }

            // Another preparer replaced or removed the row concurrently: classify the current row.
            if (attempt >= MaxPreparationAttempts)
                throw new InvalidOperationException(
                    $"JobIndex row of job '{jobId}' kept changing during publication preparation");
        }
    }

    /// <summary>
    /// Overwrites a row whose creation time differs from <paramref name="created"/>. The creation-time
    /// guard is evaluated by the database, so concurrent preparers of the same publication converge:
    /// only the first one writes, and a row that publication already advanced is never reset.
    /// </summary>
    private async Task<bool> TryReplaceWithIndexingRowAsync(string jobId, IndexPayload meta, DateTime created,
        string? methodDisplay, CancellationToken ct)
    {
        var toolId = string.IsNullOrWhiteSpace(meta.ToolId) ? "build" : meta.ToolId;
        var toolVersion = string.IsNullOrWhiteSpace(meta.ToolVersion) ? "1" : meta.ToolVersion;
        var updated = await db.JobIndices
            .Where(x => x.JobId == jobId && x.CreatedAtUtc != created)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.OrgSlug, meta.OrgSlug)
                .SetProperty(x => x.DsSlug, meta.DsSlug)
                .SetProperty(x => x.Hash, meta.Hash)
                .SetProperty(x => x.Path, meta.Path)
                .SetProperty(x => x.UserId, meta.UserId)
                .SetProperty(x => x.Queue, meta.Queue)
                .SetProperty(x => x.CreatedAtUtc, created)
                .SetProperty(x => x.LastStateChangeUtc, created)
                .SetProperty(x => x.CurrentState, IndexingStateName)
                .SetProperty(x => x.MethodDisplay, methodDisplay)
                .SetProperty(x => x.ToolId, toolId)
                .SetProperty(x => x.ToolVersion, toolVersion)
                .SetProperty(x => x.RequestHash, meta.RequestHash)
                .SetProperty(x => x.ParentJobId, meta.ParentJobId)
                .SetProperty(x => x.WorkflowExecutionId, meta.WorkflowExecutionId)
                .SetProperty(x => x.ProcessingAtUtc, (DateTime?)null)
                .SetProperty(x => x.SucceededAtUtc, (DateTime?)null)
                .SetProperty(x => x.FailedAtUtc, (DateTime?)null)
                .SetProperty(x => x.DeletedAtUtc, (DateTime?)null)
                .SetProperty(x => x.ScheduledAtUtc, (DateTime?)null)
                .SetProperty(x => x.ProgressPercent, (int?)null)
                .SetProperty(x => x.PhaseMessage, (string?)null)
                .SetProperty(x => x.ArtifactSizeBytes, (long?)null)
                .SetProperty(x => x.ArtifactSha256, (string?)null)
                .SetProperty(x => x.ErrorType, (string?)null)
                .SetProperty(x => x.LogTailJson, (string?)null)
                .SetProperty(x => x.ProgressUpdatedAtUtc, (DateTime?)null), ct);
        return updated > 0;
    }

    // Field set of a freshly prepared row: identity from the payload, Indexing state at the original
    // creation time and no telemetry (mirrors the reset in UpsertOnEnqueueAsync).
    private static void ApplyFreshIndexingRow(JobIndex row, IndexPayload meta, DateTime created, string? methodDisplay)
    {
        row.OrgSlug = meta.OrgSlug;
        row.DsSlug = meta.DsSlug;
        row.Hash = meta.Hash;
        row.Path = meta.Path;
        row.UserId = meta.UserId;
        row.Queue = meta.Queue;
        row.CreatedAtUtc = created;
        row.LastStateChangeUtc = created;
        row.CurrentState = IndexingStateName;
        row.MethodDisplay = methodDisplay;
        row.ToolId = string.IsNullOrWhiteSpace(meta.ToolId) ? "build" : meta.ToolId;
        row.ToolVersion = string.IsNullOrWhiteSpace(meta.ToolVersion) ? "1" : meta.ToolVersion;
        row.RequestHash = meta.RequestHash;
        row.ParentJobId = meta.ParentJobId;
        row.WorkflowExecutionId = meta.WorkflowExecutionId;
        row.ProcessingAtUtc = null;
        row.SucceededAtUtc = null;
        row.FailedAtUtc = null;
        row.DeletedAtUtc = null;
        row.ScheduledAtUtc = null;
        row.ProgressPercent = null;
        row.PhaseMessage = null;
        row.ArtifactSizeBytes = null;
        row.ArtifactSha256 = null;
        row.ErrorType = null;
        row.LogTailJson = null;
        row.ProgressUpdatedAtUtc = null;
    }

    public async Task ResetForRequeueAsync(string jobId, CancellationToken ct = default)
    {
        var ji = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        // Called AFTER an accepted re-queue: the row is still "Failed" (async stamp not yet
        // visible) or freshly "Enqueued". Refuse anything else - a row already at a live/terminal
        // state belongs to a fresh/final run and its fields must not be wiped.
        if (ji is null || (ji.CurrentState != "Failed" && ji.CurrentState != "Enqueued"))
            return;

        // Mirror the reset block in UpsertOnEnqueueAsync so a re-queued run starts clean.
        ji.ProcessingAtUtc = null;
        ji.SucceededAtUtc = null;
        ji.FailedAtUtc = null;
        ji.DeletedAtUtc = null;
        ji.ScheduledAtUtc = null;
        ji.ProgressPercent = null;
        ji.PhaseMessage = null;
        ji.ArtifactSizeBytes = null;
        ji.ArtifactSha256 = null;
        ji.ErrorType = null;
        ji.LogTailJson = null;
        ji.ProgressUpdatedAtUtc = null;

        await db.SaveChangesAsync(ct);

        if (ji.OrgSlug != null && ji.DsSlug != null)
            await InvalidateTasksListCacheAsync(ji.OrgSlug, ji.DsSlug, ct);
    }

    public async Task UpdateStateAsync(string jobId, string newState, DateTime changedAtUtc,
        CancellationToken ct = default)
    {
        var ji = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (ji is null)
        {
            // We are not interested in creating a new entry here if it doesn't exist
            log.LogInformation("JobIndexWriter.UpdateStateAsync: no existing JobIndex for job {JobId}", jobId);
            return;
        }

        ApplyStateChange(ji, newState, changedAtUtc);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Do not leave the failed update tracked in the reused scoped context.
            db.Entry(ji).State = EntityState.Detached;
            throw;
        }

        // Invalidate task-list cache for the affected dataset
        if (ji.OrgSlug != null && ji.DsSlug != null)
            await InvalidateTasksListCacheAsync(ji.OrgSlug, ji.DsSlug, ct);
    }

    public async Task<bool> UpdateStateIfCurrentAsync(string jobId, IReadOnlyCollection<string> expectedCurrentStates,
        string newState, DateTime changedAtUtc, CancellationToken ct = default)
    {
        var expected = expectedCurrentStates.ToArray();

        var ji = await db.JobIndices.AsNoTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (ji is null || !expected.Contains(ji.CurrentState))
            return false;

        // The expected-state guard is evaluated by the database, so a row a concurrent writer
        // (e.g. JobIndexStateFilter) already advanced is never moved back. Setters mirror
        // ApplyStateChange.
        var updated = await db.JobIndices
            .Where(x => x.JobId == jobId && expected.Contains(x.CurrentState))
            .ExecuteUpdateAsync(s =>
            {
                s.SetProperty(x => x.CurrentState, newState)
                    .SetProperty(x => x.LastStateChangeUtc, changedAtUtc);
                if (newState == ProcessingState.StateName)
                    s.SetProperty(x => x.ProcessingAtUtc, changedAtUtc)
                        .SetProperty(x => x.ErrorType, (string?)null);
                else if (newState == SucceededState.StateName)
                    s.SetProperty(x => x.SucceededAtUtc, changedAtUtc)
                        .SetProperty(x => x.ErrorType, (string?)null);
                else if (newState == FailedState.StateName)
                    s.SetProperty(x => x.FailedAtUtc, changedAtUtc);
                else if (newState == DeletedState.StateName)
                    s.SetProperty(x => x.DeletedAtUtc, changedAtUtc);
                else if (newState == ScheduledState.StateName)
                    s.SetProperty(x => x.ScheduledAtUtc, changedAtUtc);
            }, ct);

        // A set-based update bypasses the change tracker: drop any stale tracked copy.
        var local = db.JobIndices.Local.FirstOrDefault(x => x.JobId == jobId);
        if (local != null)
            db.Entry(local).State = EntityState.Detached;

        if (updated == 0)
            return false;

        if (ji.OrgSlug != null && ji.DsSlug != null)
            await InvalidateTasksListCacheAsync(ji.OrgSlug, ji.DsSlug, ct);
        return true;
    }

    // State stamping of UpdateStateAsync; the set-based update of UpdateStateIfCurrentAsync mirrors it.
    private static void ApplyStateChange(JobIndex ji, string newState, DateTime changedAtUtc)
    {
        ji.CurrentState = newState;
        ji.LastStateChangeUtc = changedAtUtc;
        // A fresh attempt (Processing) or a recovery (Succeeded) supersedes any transient error a
        // prior attempt recorded (e.g. a DdbBusyException auto-retried to success).
        if (newState == ProcessingState.StateName)
        {
            ji.ProcessingAtUtc = changedAtUtc;
            ji.ErrorType = null;
        }
        else if (newState == SucceededState.StateName)
        {
            ji.SucceededAtUtc = changedAtUtc;
            ji.ErrorType = null;
        }
        else if (newState == FailedState.StateName)
            ji.FailedAtUtc = changedAtUtc;
        else if (newState == DeletedState.StateName)
            ji.DeletedAtUtc = changedAtUtc;
        else if (newState == ScheduledState.StateName)
            ji.ScheduledAtUtc = changedAtUtc;
    }

    private static readonly string[] TerminalStates = ["Succeeded", "Failed", "Deleted"];

    public async Task<int> DeleteTerminalBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        // Bulk delete may affect multiple datasets - scan for affected org/ds pairs first
        var affectedOrgDs = new HashSet<(string org, string ds)>();
        var toCheck = await db.JobIndices
            .Where(j => TerminalStates.Contains(j.CurrentState) && j.LastStateChangeUtc < cutoffUtc)
            .Select(j => new { j.OrgSlug, j.DsSlug })
            .ToListAsync(ct);

        foreach (var r in toCheck)
            if (r.OrgSlug != null && r.DsSlug != null)
                affectedOrgDs.Add((r.OrgSlug, r.DsSlug));

        var deleted = await db.JobIndices
            .Where(j => TerminalStates.Contains(j.CurrentState) && j.LastStateChangeUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct);

        foreach (var pair in affectedOrgDs)
            await InvalidateTasksListCacheAsync(pair.org, pair.ds, ct);

        return deleted;
    }

    public async Task<IReadOnlyList<string>> DeleteTerminalForDatasetAsync(string orgSlug, string dsSlug,
        string? toolId = null, CancellationToken ct = default)
    {
        var query = db.JobIndices
            .Where(j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug && TerminalStates.Contains(j.CurrentState));

        if (!string.IsNullOrWhiteSpace(toolId))
            query = query.Where(j => j.ToolId == toolId);

        var ids = await query.Select(j => j.JobId).ToListAsync(ct);
        if (ids.Count == 0)
            return ids;

        await db.JobIndices.Where(j => ids.Contains(j.JobId)).ExecuteDeleteAsync(ct);

        // Invalidate task-list cache for this dataset
        await InvalidateTasksListCacheAsync(orgSlug, dsSlug, ct);

        return ids;
    }

    public async Task<(bool deleted, string? orgSlug, string? dsSlug)> DeleteTerminalJobByIdAsync(
        string jobId, CancellationToken ct = default)
    {
        var ji = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (ji is null)
            return (false, null, null);

        if (!TerminalStates.Contains(ji.CurrentState))
        {
            log.LogInformation("JobIndexWriter.DeleteTerminalJobByIdAsync: job {JobId} is in non-terminal state '{State}', skipping", jobId, ji.CurrentState);
            return (false, null, null);
        }

        var orgSlug = ji.OrgSlug;
        var dsSlug = ji.DsSlug;

        db.JobIndices.Remove(ji);
        await db.SaveChangesAsync(ct);

        if (orgSlug != null && dsSlug != null)
            await InvalidateTasksListCacheAsync(orgSlug, dsSlug, ct);

        log.LogInformation("Deleted terminal JobIndex record for job {JobId} in {Org}/{Ds}", jobId, orgSlug, dsSlug);
        return (true, orgSlug, dsSlug);
    }

    public async Task UpdateProgressAsync(string jobId, int? percent, string? phaseMessage,
        string? logTailJson, DateTime updatedAtUtc, CancellationToken ct = default)
    {
        var ji = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (ji is null)
        {
            log.LogInformation("JobIndexWriter.UpdateProgressAsync: no existing JobIndex for job {JobId}", jobId);
            return;
        }

        if (percent.HasValue)
            ji.ProgressPercent = percent;
        if (phaseMessage is not null)
            ji.PhaseMessage = phaseMessage;
        if (logTailJson is not null)
            ji.LogTailJson = logTailJson;
        ji.ProgressUpdatedAtUtc = updatedAtUtc;

        await db.SaveChangesAsync(ct);

        // Invalidate task-list cache for the affected dataset
        if (ji.OrgSlug != null && ji.DsSlug != null)
            await InvalidateTasksListCacheAsync(ji.OrgSlug, ji.DsSlug, ct);
    }

    public async Task UpdateArtifactAsync(string jobId, long sizeBytes, string? sha256,
        CancellationToken ct = default)
    {
        var ji = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (ji is null)
        {
            log.LogInformation("JobIndexWriter.UpdateArtifactAsync: no existing JobIndex for job {JobId}", jobId);
            return;
        }

        ji.ArtifactSizeBytes = sizeBytes;
        ji.ArtifactSha256 = sha256;

        await db.SaveChangesAsync(ct);

        if (ji.OrgSlug != null && ji.DsSlug != null)
            await InvalidateTasksListCacheAsync(ji.OrgSlug, ji.DsSlug, ct);
    }

    public async Task UpdateErrorAsync(string jobId, string errorType, CancellationToken ct = default)
    {
        var ji = await db.JobIndices.AsTracking().FirstOrDefaultAsync(x => x.JobId == jobId, ct);
        if (ji is null)
        {
            log.LogInformation("JobIndexWriter.UpdateErrorAsync: no existing JobIndex for job {JobId}", jobId);
            return;
        }

        ji.ErrorType = errorType;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Do not leave the failed update tracked in the reused scoped context.
            db.Entry(ji).State = EntityState.Detached;
            throw;
        }

        if (ji.OrgSlug != null && ji.DsSlug != null)
            await InvalidateTasksListCacheAsync(ji.OrgSlug, ji.DsSlug, ct);
    }

    /// <summary>
    /// Removes the per-dataset task-list cache entry.
    /// Used after every mutation to ensure pollers get fresh data immediately.
    /// </summary>
    private async Task InvalidateTasksListCacheAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var cacheKey = CacheCategories.TasksList(orgSlug, dsSlug);
        // Logs and swallows a cache error itself; a list being computed now is not stored
        await cache.RemoveAsync(cacheKey, ct);

        // After the invalidation: a listener that refreshes now reads the new list
        notifier?.Publish(orgSlug, dsSlug);
    }
}
