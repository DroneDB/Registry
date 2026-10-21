#nullable enable
using System;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Ports.DroneDB;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire state filter that reacts to build jobs entering the Failed state.
/// When a BuildWrapper / BuildPendingWrapper job exhausts its retries and is
/// marked as failed, observations are refreshed from native state while the
/// affected dataset's bounded retry controls are preserved.
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
            if (context.NewState is not FailedState failedState) return;

            var methodName = context.BackgroundJob?.Job?.Method?.Name;
            if (string.IsNullOrEmpty(methodName)) return;
            if (Array.IndexOf(BuildMethodNames, methodName) < 0) return;

            var jobId = context.BackgroundJob!.Id;
            var conn = context.Connection;
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
                "BuildJobFailureFilter: build job {JobId} ({Method}) for {Org}/{Ds} entered Failed state; refreshing build-pending observations",
                jobId, methodName, orgSlug, dsSlug);

            using var scope = sp.CreateScope();
            // Persist WHY the build failed: JobIndex.ErrorType = bare exception type name,
            // same convention as heavy tasks (HeavyTaskJobWrapper). The message itself lives
            // in the log tail persisted by BuildLogCaptureFilter; here we only record the
            // class (native failures surface as DdbException). Synchronous filter ->
            // GetAwaiter().GetResult() like the observation refresh below.
            // Runs only when org/ds params are present (above early return): the writer
            // also busts the org/ds-scoped tasks-list cache. Errors stay inside the outer
            // try/catch so logging-path failures never break state transitions.
            var errorType = failedState.Exception?.GetType().Name ?? "Failed";
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            writer.UpdateErrorAsync(jobId, errorType).GetAwaiter().GetResult();
            log.LogInformation(
                "BuildJobFailureFilter: job {JobId} ErrorType recorded as '{ErrorType}'",
                jobId, errorType);

            var pending = scope.ServiceProvider.GetRequiredService<BuildPendingService>();
            pending.RefreshPendingStatus(orgSlug, dsSlug, context.Storage).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "BuildJobFailureFilter.OnStateApplied: error recording failure or refreshing pending observations for job {JobId}",
                context.BackgroundJob?.Id);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
