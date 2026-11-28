using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.BuildNotifications;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire state filter that starts a new OGC cache generation (<see cref="IOgcCacheKeys"/>) of a dataset when a job
/// that built in it ends, succeeded or failed: the cached layer lists and capabilities say which rasters have a COG
/// and which vectors a GeoPackage, so a raster whose COG was just built is offered at once instead of when they
/// expire. The jobs are a build (of an entry or of the whole dataset), a background rebuild, a mask borders (which
/// builds the masked raster) and a build task.
/// </summary>
public sealed class BuildOgcCacheFilter(
    IOgcCacheKeys ogcKeys,
    ILogger<BuildOgcCacheFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        StateFilterGuard.Run(log, context, "renewing the OGC caches of the dataset", () =>
        {
            if (context.NewState is not (SucceededState or FailedState) || context.BackgroundJob?.Job == null ||
                !RanBuild(context.BackgroundJob, context.Connection))
                return;

            var jobId = context.BackgroundJob.Id;
            var orgSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.OrgSlug);
            var dsSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.DsSlug);
            if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug))
                return;

            // Synchronous filter: one write of the generation in the cache
            ogcKeys.InvalidateAsync(orgSlug, dsSlug).GetAwaiter().GetResult();
        });

    private static bool RanBuild(BackgroundJob job, IStorageConnection connection) =>
        DdbJobDescriptor.Of(job.Job) is { RunsNativeBuild: true } || FinishedBuildJob.BuildsEntry(job, connection);

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
