#nullable enable
using System;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Services;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire state filter that reacts to build jobs entering the Failed state.
/// When a BuildWrapper / BuildPendingWrapper job exhausts its retries and is
/// marked as failed, any cached "build pending" tracker entries for the
/// affected dataset are invalidated so that subsequent build-pending checks
/// re-read the authoritative state from disk instead of returning stale
/// "build in progress" results.
/// </summary>
public sealed class BuildJobFailureFilter(
    IServiceProvider sp,
    ILogger<BuildJobFailureFilter> log) : IApplyStateFilter
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
            if (context.NewState is not FailedState) return;

            var methodName = context.BackgroundJob?.Job?.Method?.Name;
            if (string.IsNullOrEmpty(methodName)) return;
            if (Array.IndexOf(BuildMethodNames, methodName) < 0) return;

            var jobId = context.BackgroundJob!.Id;
            var conn = Hangfire.JobStorage.Current.GetConnection();
            var orgSlug = conn.GetJobParameter(jobId, JobParamKeys.OrgSlug);
            var dsSlug = conn.GetJobParameter(jobId, JobParamKeys.DsSlug);

            if (string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug))
            {
                log.LogDebug(
                    "BuildJobFailureFilter: job {JobId} failed but missing org/ds slug parameters; skipping cache invalidation",
                    jobId);
                return;
            }

            log.LogWarning(
                "BuildJobFailureFilter: build job {JobId} ({Method}) for {Org}/{Ds} entered Failed state; invalidating build-pending cache",
                jobId, methodName, orgSlug, dsSlug);

            using var scope = sp.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<ICacheManager>();
            var category = CacheCategories.ForDataset(orgSlug, dsSlug);
            cache.RemoveByCategoryAsync(MagicStrings.BuildPendingTrackerCacheSeed, category)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "BuildJobFailureFilter.OnStateApplied: error while invalidating cache for job {JobId}",
                context.BackgroundJob?.Id);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
