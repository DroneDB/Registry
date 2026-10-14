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
/// <see cref="Utilities.HangfireUtils.BuildPendingWrapper(Registry.Ports.DroneDB.IDDB, Hangfire.Server.PerformContext)"/>) reaches a terminal
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
            if (context.NewState is not SucceededState && context.NewState is not FailedState) return;

            var methodName = context.BackgroundJob?.Job?.Method?.Name;
            if (string.IsNullOrEmpty(methodName)) return;
            if (Array.IndexOf(BuildMethodNames, methodName) < 0) return;
            // Only a failed entry with its own retained native marker is recoverable.
            if (context.NewState is FailedState && methodName != nameof(Utilities.HangfireUtils.BuildWrapper)) return;

            var jobId = context.BackgroundJob!.Id;
            var conn = context.Connection;
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
            string? failedPath = null, failedHash = null;
            if (context.NewState is FailedState)
            {
                failedPath = context.BackgroundJob.Job.Args.Count > 1
                    ? context.BackgroundJob.Job.Args[1] as string : null;
                failedHash = conn.GetJobParameter(jobId, JobParamKeys.Hash);
                if (string.IsNullOrWhiteSpace(failedPath) || string.IsNullOrWhiteSpace(failedHash)) return;
            }
            buildPending.ScheduleRetryIfPending(orgSlug, dsSlug, jobId, failedPath, failedHash, context.Storage)
                .GetAwaiter().GetResult();
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
