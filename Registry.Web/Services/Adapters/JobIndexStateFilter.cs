#nullable enable
using System;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Updates the JobIndex on every state change (Processing, Succeeded, Failed, Deleted, Scheduled, etc.).
/// </summary>
public sealed class JobIndexStateFilter(IServiceProvider sp, ILogger<JobIndexStateFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        try
        {
            using var scope = sp.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            writer.UpdateStateAsync(context.BackgroundJob.Id, context.NewState.Name, DateTime.UtcNow).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "JobIndexStateFilter.OnStateApplied: error while updating state for job {JobId}", context.BackgroundJob.Id);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // Not needed: we only care about the new state being applied
        log.LogDebug("JobIndexStateFilter.OnStateUnapplied: job {JobId} state unapplied to {NewState}",
            context.BackgroundJob?.Id,
            context.NewState?.Name ?? "<null>");
        
    }
}