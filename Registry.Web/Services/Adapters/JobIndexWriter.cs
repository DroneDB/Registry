using System;
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
                MethodDisplay = methodDisplay
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
}
