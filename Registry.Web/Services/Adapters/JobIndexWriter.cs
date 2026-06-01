using System;
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
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

#nullable enable

public class JobIndexWriter(RegistryContext db, ILogger<JobIndexWriter> log) : IJobIndexWriter
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

        ji.CurrentState = newState;
        ji.LastStateChangeUtc = changedAtUtc;
        if (newState == ProcessingState.StateName)
            ji.ProcessingAtUtc = changedAtUtc;
        else if (newState == SucceededState.StateName)
            ji.SucceededAtUtc = changedAtUtc;
        else if (newState == FailedState.StateName)
            ji.FailedAtUtc = changedAtUtc;
        else if (newState == DeletedState.StateName)
            ji.DeletedAtUtc = changedAtUtc;
        else if (newState == ScheduledState.StateName)
            ji.ScheduledAtUtc = changedAtUtc;

        await db.SaveChangesAsync(ct);
    }

    private static readonly string[] TerminalStates = ["Succeeded", "Failed", "Deleted"];

    public async Task<int> DeleteTerminalBeforeAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        try
        {
            // Prefer bulk delete (EF Core 7+) - works with relational providers (MySQL, SQLite, etc.)
            var deleted = await db.JobIndices
                .Where(j => TerminalStates.Contains(j.CurrentState) && j.LastStateChangeUtc < cutoffUtc)
                .ExecuteDeleteAsync(ct);

            return deleted;
        }
        catch (Exception ex)
        {

            log.LogWarning(ex, "Bulk delete failed in JobIndexWriter.DeleteTerminalBeforeAsync, falling back to client-side deletion");
            // Fallback for providers that don't support ExecuteDeleteAsync (e.g., InMemory in tests)
            var toDelete = await db.JobIndices
                .Where(j => TerminalStates.Contains(j.CurrentState) && j.LastStateChangeUtc < cutoffUtc)
                .ToListAsync(ct);

            db.JobIndices.RemoveRange(toDelete);
            await db.SaveChangesAsync(ct);

            return toDelete.Count;
        }
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

        await db.SaveChangesAsync(ct);
    }
}
