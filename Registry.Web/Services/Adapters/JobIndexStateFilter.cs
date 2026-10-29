using System;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Filters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Updates the JobIndex on every state change (Processing, Succeeded, Failed, Deleted, Scheduled, etc.).
/// </summary>
public sealed class JobIndexStateFilter(IServiceScopeFactory scopes, ILogger<JobIndexStateFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        StateFilterGuard.Run(log, context, "updating the job index", () =>
        {
            using var scope = scopes.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            writer.UpdateStateAsync(context.BackgroundJob.Id, context.NewState.Name, DateTime.UtcNow).GetAwaiter().GetResult();
        });

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // Not needed: we only care about the new state being applied
        log.LogDebug("JobIndexStateFilter.OnStateUnapplied: job {JobId} state unapplied to {NewState}",
            context.BackgroundJob?.Id,
            context.NewState?.Name ?? "<null>");
        
    }
}