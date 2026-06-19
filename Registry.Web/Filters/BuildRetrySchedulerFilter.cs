#nullable enable
using System;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Filters;

/// <summary>
/// Event-driven replacement for the high-frequency "process-pending-builds"
/// poller. When a build job (<see cref="Utilities.HangfireUtils.BuildWrapper"/> or
/// <see cref="Utilities.HangfireUtils.BuildPendingWrapper"/>) reaches a terminal
/// state, this filter asks <see cref="BuildPendingService"/> whether the affected
/// dataset still has pending builds and, if so, self-schedules a single delayed
/// retry with exponential backoff. When nothing remains pending the chain stops,
/// so idle datasets generate zero recurring Hangfire churn.
/// </summary>
public sealed class BuildRetrySchedulerFilter(
    IServiceProvider sp,
    ILogger<BuildRetrySchedulerFilter> log) : IApplyStateFilter
{
    private static readonly string[] BuildMethodNames =
    [
        nameof(Utilities.HangfireUtils.BuildWrapper),
        nameof(Utilities.HangfireUtils.BuildPendingWrapper)
    ];

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        try
        {
            // React only to successful completion. A deferred (missing-dependency)
            // build always completes successfully and simply leaves its ".pending"
            // marker behind, so success is the correct trigger to re-evaluate and
            // schedule the next retry. Genuine build failures are handled by
            // BuildJobFailureFilter (cache invalidation) and the low-frequency
            // safety-net sweep; reacting to them here would create a tight retry
            // loop for a persistently broken build.
            if (context.NewState is not SucceededState) return;

            var methodName = context.BackgroundJob?.Job?.Method?.Name;
            if (string.IsNullOrEmpty(methodName)) return;
            if (Array.IndexOf(BuildMethodNames, methodName) < 0) return;

            var jobId = context.BackgroundJob!.Id;
            using var conn = Hangfire.JobStorage.Current.GetConnection();
            var orgSlug = conn.GetJobParameter(jobId, JobParamKeys.OrgSlug);
            var dsSlug = conn.GetJobParameter(jobId, JobParamKeys.DsSlug);

            if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug))
            {
                log.LogDebug(
                    "BuildRetrySchedulerFilter: job {JobId} ({Method}) reached terminal state but is missing org/ds parameters; skipping",
                    jobId, methodName);
                return;
            }

            using var scope = sp.CreateScope();
            var buildPending = scope.ServiceProvider.GetRequiredService<BuildPendingService>();
            buildPending.ScheduleRetryIfPending(orgSlug, dsSlug).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "BuildRetrySchedulerFilter.OnStateApplied: error scheduling pending-build retry for job {JobId}",
                context.BackgroundJob?.Id);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
