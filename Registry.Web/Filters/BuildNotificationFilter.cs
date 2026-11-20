using System.Threading;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Builds;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire state filter that hands a finished entry build (<see cref="FinishedBuildJob"/>: a build, a background
/// rebuild, a build task) to <see cref="FinishedBuildHandler"/>, on the host that applies the state: its requester's
/// build notifications get the outcome, and a build that still waits for files keeps one task. Whether the end is
/// final is libddb's answer about the entry; this filter changes nothing that answer or the other filters read. A
/// failed entry build that is run again (a user's retry keeps the job id) starts a new request
/// (<see cref="FinishedBuildHandler.RunAgainAsync"/>), so the end of that run is told even when it is the outcome
/// already sent.
/// </summary>
/// <remarks>
/// Installed after <see cref="JobIndexStateFilter"/>: who asked and which builds of the entry ended before are read from
/// the job index, whose row of this job then holds its final state. The pending builds whose retries run out are
/// reported by <see cref="BuildRetrySchedulerFilter"/>, which is where the retry budget is decided; a failure this filter
/// sees with a pending marker left is theirs.
/// </remarks>
public sealed class BuildNotificationFilter(
    IServiceScopeFactory scopes,
    ILogger<BuildNotificationFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        StateFilterGuard.Run(log, context, "handling the end of the build", () =>
        {
            var runAgain = IsRunAgainFromFailure(context);
            var build = runAgain ? null : FinishedBuildJob.Of(context.BackgroundJob, context.NewState, context.Connection);
            if (!runAgain && build == null)
                return;

            using var scope = scopes.CreateScope();
            var builds = scope.ServiceProvider.GetRequiredService<FinishedBuildHandler>();
            (build == null
                    ? builds.RunAgainAsync(context.BackgroundJob.Id, CancellationToken.None)
                    : builds.EndedAsync(build, CancellationToken.None))
                .GetAwaiter().GetResult();
        });

    // A failed entry build queued (or scheduled) to run again: a user's retry, which keeps the job id. The filters run
    // before the state change is committed, so the request is renewed before any worker can take the job, and the end of
    // the new run is never taken for the request already answered. Hangfire's own retries of an attempt never pass
    // through Failed: they stay the same request.
    private static bool IsRunAgainFromFailure(ApplyStateContext context) =>
        context.OldStateName == FailedState.StateName && context.NewState is EnqueuedState or ScheduledState &&
        FinishedBuildJob.BuildsEntry(context.BackgroundJob, context.Connection);

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
