#nullable enable
using System.Threading;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.BuildNotifications;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire state filter that reports a finished entry build (<see cref="FinishedBuildJob"/>: a build, a background
/// rebuild, a build task) to its requester's build notifications, on the host that applies the state. Whether the end is
/// final is libddb's answer about the entry (<see cref="FinishedBuildReporter"/>); this filter changes nothing that
/// answer or the other filters read.
/// </summary>
/// <remarks>
/// Installed after <see cref="JobIndexStateFilter"/>: who asked is read from the job index, whose row of this job then
/// holds its final state. The pending builds whose retries run out are reported by <see cref="BuildRetrySchedulerFilter"/>,
/// which is where the retry budget is decided; a failure this filter sees with a pending marker left is theirs.
/// </remarks>
public sealed class BuildNotificationFilter(
    IServiceScopeFactory scopes,
    ILogger<BuildNotificationFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        StateFilterGuard.Run(log, context, "reporting the build notification", () =>
        {
            if (FinishedBuildJob.Of(context.BackgroundJob, context.NewState, context.Connection) is not { } build)
                return;

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<FinishedBuildReporter>()
                .ReportAsync(build, CancellationToken.None).GetAwaiter().GetResult();
        });

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
