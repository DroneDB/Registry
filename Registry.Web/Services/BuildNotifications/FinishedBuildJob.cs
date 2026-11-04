using System;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Jobs;

namespace Registry.Web.Services.BuildNotifications;

/// <summary>
/// A job that built one entry and just ended: a build or a background rebuild (<see cref="DdbJobDescriptor.BuildsEntry"/>,
/// either job shape) or a build task (the <see cref="HeavyToolIds.Build"/> tool, which runs the same build inline). The
/// whole-dataset build, the pending-build coordinator and the other dataset jobs are not one: they report no entry.
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="OrgSlug">The organization.</param>
/// <param name="DsSlug">The dataset.</param>
/// <param name="Path">The entry.</param>
/// <param name="Hash">The hash the job was queued for; null when it carries none.</param>
/// <param name="UserId">The user the job carries.</param>
/// <param name="Failure">Why it failed; null when it succeeded.</param>
public sealed record FinishedBuildJob(
    string JobId,
    string OrgSlug,
    string DsSlug,
    string Path,
    string? Hash,
    string? UserId,
    Exception? Failure)
{
    /// <summary>Whether the job succeeded.</summary>
    public bool Succeeded => Failure == null;

    /// <summary>
    /// <paramref name="job"/> entering <paramref name="newState"/> as a finished entry build, its parameters read from
    /// <paramref name="connection"/>; null when it is not one (another job, another state, no entry or dataset).
    /// </summary>
    /// <remarks>
    /// The final state of a job, not an attempt: an attempt Hangfire retries is Scheduled again, never Failed, so a build
    /// task (whose class policy retries a busy index) is judged once, like a build job.
    /// </remarks>
    public static FinishedBuildJob? Of(BackgroundJob? job, IState? newState, IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (job?.Job == null || newState is not (SucceededState or FailedState))
            return null;

        var path = EntryOf(job, connection);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var orgSlug = connection.GetJobParameter(job.Id, JobParamKeys.OrgSlug);
        var dsSlug = connection.GetJobParameter(job.Id, JobParamKeys.DsSlug);
        if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug))
            return null;

        return new FinishedBuildJob(job.Id, orgSlug, dsSlug, path, NullIfBlank(connection.GetJobParameter(job.Id, JobParamKeys.Hash)),
            NullIfBlank(connection.GetJobParameter(job.Id, JobParamKeys.UserId)), (newState as FailedState)?.Exception);
    }

    /// <summary>Whether <paramref name="job"/> builds one entry, whatever its state: a job whose end <see cref="Of"/> reports.</summary>
    public static bool BuildsEntry(BackgroundJob? job, IStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return job?.Job != null && !string.IsNullOrWhiteSpace(EntryOf(job, connection));
    }

    // The entry the job builds; null when it builds none (another job, the whole dataset, a task without a path)
    private static string? EntryOf(BackgroundJob job, IStorageConnection connection)
    {
        if (DdbJobDescriptor.Of(job.Job) is { } descriptor)
            return descriptor.BuildsEntry ? descriptor.Path : null;

        return IsBuildTask(job.Job) ? connection.GetJobParameter(job.Id, JobParamKeys.Path) : null;
    }

    // HeavyTaskJobWrapper.Run(toolId, toolVersion, requestJson, ...)
    private static bool IsBuildTask(Hangfire.Common.Job job) =>
        job.Type == typeof(HeavyTaskJobWrapper) && job.Method.Name == nameof(HeavyTaskJobWrapper.Run) &&
        job.Args.Count > 0 && job.Args[0] is HeavyToolIds.Build;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
