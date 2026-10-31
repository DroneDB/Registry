using System.Threading;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Filters;

/// <summary>
/// Event-driven replacement for the high-frequency "process-pending-builds"
/// poller. When a build job (<see cref="DdbJobs.Build"/>, <see cref="DdbJobs.RebuildInBackground"/> or
/// <see cref="DdbJobs.BuildPending"/>, either shape: <see cref="DdbJobDescriptor"/>) reaches a terminal state, this filter asks <see cref="BuildPendingService"/> whether the affected
/// dataset still has pending builds and, if so, self-schedules a single delayed
/// retry with exponential backoff. When nothing remains pending the chain stops,
/// so idle datasets generate zero recurring Hangfire churn. When the retry budget runs out, the pending entries failed for
/// good: their requesters' build notifications get them (<see cref="FinishedBuildReporter.ReportGivenUpAsync"/>).
/// </summary>
public sealed class BuildRetrySchedulerFilter(
    IServiceScopeFactory scopes,
    ILogger<BuildRetrySchedulerFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        StateFilterGuard.Run(log, context, "scheduling the pending-build retry", () =>
        {
            if (context.NewState is not SucceededState && context.NewState is not FailedState) return;

            if (DdbJobDescriptor.Of(context.BackgroundJob?.Job) is not { IsBuild: true } job) return;
            var methodName = context.BackgroundJob!.Job.Method.Name;
            // Only a failed entry build (a build or a background rebuild) with its own retained native marker is
            // recoverable; the retry re-enqueues it on its own queue (PendingBuildEnqueuer).
            if (context.NewState is FailedState && !job.BuildsEntry) return;

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

            using var scope = scopes.CreateScope();
            var buildPending = scope.ServiceProvider.GetRequiredService<BuildPendingService>();
            string? failedPath = null, failedHash = null;
            if (context.NewState is FailedState)
            {
                failedPath = job.Path;
                failedHash = conn.GetJobParameter(jobId, JobParamKeys.Hash);
                if (string.IsNullOrWhiteSpace(failedPath) || string.IsNullOrWhiteSpace(failedHash)) return;
            }
            var givenUp = buildPending.ScheduleRetryIfPending(orgSlug, dsSlug, jobId, failedPath, failedHash,
                context.Storage).GetAwaiter().GetResult();

            // The retries of these entries ran out: their failure is final, so their requesters are told now
            if (givenUp.Count > 0)
                scope.ServiceProvider.GetRequiredService<FinishedBuildReporter>()
                    .ReportGivenUpAsync(orgSlug, dsSlug, givenUp, CancellationToken.None).GetAwaiter().GetResult();
        });

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
