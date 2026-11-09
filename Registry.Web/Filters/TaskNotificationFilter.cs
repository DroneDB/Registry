using System.Threading;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.TaskNotifications;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire state filter that tells the user who submitted a heavy task that it succeeded or failed (FR-PRC-40, D-012,
/// <see cref="TaskNotifier"/>), on the host that applies the state. It changes nothing that the other filters read: a
/// failure of its work is logged and the state change goes on (<see cref="StateFilterGuard"/>). A failed task queued to run
/// again (a user's retry keeps the job id) starts a new request, so the end of that run is told too.
/// </summary>
/// <remarks>
/// Installed after <see cref="JobIndexStateFilter"/>: who asked, the files, the error and the times are read from the job
/// index, whose row of this job then holds its final state.
/// </remarks>
public sealed class TaskNotificationFilter(
    IServiceScopeFactory scopes,
    ILogger<TaskNotificationFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        StateFilterGuard.Run(log, context, "reporting the task notification", () =>
        {
            var runsAgain = FinishedTask.RunsAgainFromFailure(context.BackgroundJob, context.OldStateName, context.NewState);
            var task = runsAgain ? null : FinishedTask.Of(context.BackgroundJob, context.NewState);
            if (!runsAgain && task == null)
                return;

            using var scope = scopes.CreateScope();
            var notifier = scope.ServiceProvider.GetRequiredService<TaskNotifier>();
            (task == null
                    ? notifier.RunAgainAsync(context.BackgroundJob.Id, CancellationToken.None)
                    : notifier.NotifyAsync(task, CancellationToken.None))
                .GetAwaiter().GetResult();
        });

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
