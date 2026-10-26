#nullable enable
using System;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// The one way to queue a dataset job (<see cref="DdbJobs"/>): as an indexed job that names its
/// dataset by <see cref="DatasetKey"/> (no DroneDB object in the job arguments).
/// </summary>
public static class QueueDdbJobs
{
    /// <summary>Queues a build of <paramref name="path"/> (null: the whole dataset).</summary>
    /// <returns>The job id.</returns>
    public static string EnqueueBuild(this IBackgroundJobsProcessor jobs, DatasetKey dataset, string? path,
        bool force, IndexPayload meta)
    {
        var (orgSlug, internalRef) = Target(dataset);
        return jobs.EnqueueIndexed<DdbJobs>(j => j.Build(orgSlug, internalRef, path, force, null), meta);
    }

    /// <summary>
    /// Queues the background rebuild of <paramref name="path"/>: a build no user waits for, on its own queue
    /// (<see cref="DdbJobs.RebuildInBackground"/>).
    /// </summary>
    /// <returns>The job id.</returns>
    public static string EnqueueRebuild(this IBackgroundJobsProcessor jobs, DatasetKey dataset, string path,
        IndexPayload meta)
    {
        var (orgSlug, internalRef) = Target(dataset);
        return jobs.EnqueueIndexed<DdbJobs>(j => j.RebuildInBackground(orgSlug, internalRef, path, null), meta);
    }

    /// <summary>Queues the pending-build job.</summary>
    public static string EnqueueBuildPending(this IBackgroundJobsProcessor jobs, DatasetKey dataset, IndexPayload meta)
    {
        var (orgSlug, internalRef) = Target(dataset);
        return jobs.EnqueueIndexed<DdbJobs>(j => j.BuildPending(orgSlug, internalRef, null), meta);
    }

    /// <summary>Schedules the pending-build job in <paramref name="delay"/>.</summary>
    public static string ScheduleBuildPending(this IBackgroundJobsProcessor jobs, DatasetKey dataset,
        IndexPayload meta, TimeSpan delay)
    {
        var (orgSlug, internalRef) = Target(dataset);
        return jobs.ScheduleIndexed<DdbJobs>(j => j.BuildPending(orgSlug, internalRef, null), meta, delay);
    }

    /// <summary>Queues the dataset cleanup (gone entries, orphaned build artifacts).</summary>
    public static string EnqueueCleanup(this IBackgroundJobsProcessor jobs, DatasetKey dataset, IndexPayload meta)
    {
        var (orgSlug, internalRef) = Target(dataset);
        return jobs.EnqueueIndexed<DdbJobs>(j => j.Cleanup(orgSlug, internalRef, null), meta);
    }

    /// <summary>Queues the mask-borders job of <paramref name="inputPath"/>.</summary>
    public static string EnqueueMaskBorders(this IBackgroundJobsProcessor jobs, DatasetKey dataset, string inputPath,
        string outputPath, int nearDist, bool white, IndexPayload meta)
    {
        var (orgSlug, internalRef) = Target(dataset);
        return jobs.EnqueueIndexed<DdbJobs>(
            j => j.MaskBorders(orgSlug, internalRef, inputPath, outputPath, nearDist, white, null), meta);
    }

    private static (string OrgSlug, Guid InternalRef) Target(DatasetKey dataset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset.OrgSlug, nameof(dataset));
        if (dataset.InternalRef == Guid.Empty)
            throw new ArgumentException("The dataset has no InternalRef", nameof(dataset));
        return (dataset.OrgSlug, dataset.InternalRef);
    }
}
