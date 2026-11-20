using System;
using Hangfire;
using Hangfire.States;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;

namespace Registry.Web.Services.TaskNotifications;

/// <summary>
/// A heavy task (<see cref="HeavyTaskJobWrapper"/>) that just ended for good, succeeded or failed (FR-PRC-40). The build
/// tool is not one: its end is a build notification (<see cref="BuildNotifications.FinishedBuildJob"/>). A task that
/// Hangfire retries passes through Scheduled, never Failed, so only the end of the task is seen, once.
/// </summary>
/// <param name="JobId">The task.</param>
/// <param name="ToolId">The tool it ran.</param>
/// <param name="Failure">Why it failed; null when it succeeded.</param>
public sealed record FinishedTask(string JobId, string ToolId, Exception? Failure)
{
    /// <summary>Whether the task succeeded.</summary>
    public bool Succeeded => Failure == null;

    /// <summary>
    /// <paramref name="job"/> entering <paramref name="newState"/> as the end of a heavy task other than the build; null
    /// when it is not one (another job, another state, the build tool).
    /// </summary>
    public static FinishedTask? Of(BackgroundJob? job, IState? newState)
    {
        if (job?.Job == null || newState is not (SucceededState or FailedState) || ToolOf(job.Job) is not { } toolId)
            return null;

        return toolId == HeavyToolIds.Build ? null : new FinishedTask(job.Id, toolId, (newState as FailedState)?.Exception);
    }

    /// <summary>Whether a task other than the build entering <paramref name="newState"/> from a failure runs again (a user's retry).</summary>
    public static bool RunsAgainFromFailure(BackgroundJob? job, string? oldStateName, IState? newState) =>
        oldStateName == FailedState.StateName && newState is EnqueuedState or ScheduledState && job?.Job != null &&
        ToolOf(job.Job) is { } toolId && toolId != HeavyToolIds.Build;

    // HeavyTaskJobWrapper.Run(toolId, toolVersion, requestJson, ...): the tool id; null for another job
    private static string? ToolOf(Hangfire.Common.Job job) =>
        job.Type == typeof(HeavyTaskJobWrapper) && job.Method.Name == nameof(HeavyTaskJobWrapper.Run) &&
        job.Args.Count > 0 && job.Args[0] is string toolId && !string.IsNullOrWhiteSpace(toolId)
            ? toolId
            : null;
}
