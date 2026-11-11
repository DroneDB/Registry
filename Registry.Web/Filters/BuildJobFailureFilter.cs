using System;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire state filter that reacts to build jobs entering the Failed state.
/// When a build job (<see cref="DdbJobDescriptor.IsBuild"/>) exhausts its retries and is
/// marked as failed, observations are refreshed from native state while the
/// affected dataset's bounded retry controls are preserved.
/// </summary>
public sealed class BuildJobFailureFilter(
    IServiceScopeFactory scopes,
    ILogger<BuildJobFailureFilter> log) : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        StateFilterGuard.Run(log, context, "recording the failure or refreshing the pending observations", () =>
        {
            if (context.NewState is not FailedState failedState) return;

            if (DdbJobDescriptor.Of(context.BackgroundJob?.Job) is not { IsBuild: true }) return;
            var methodName = context.BackgroundJob!.Job.Method.Name;

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

            using var scope = scopes.CreateScope();
            // Persist WHY the build failed, as heavy tasks do (HeavyTaskJobWrapper): JobIndex.ErrorType =
            // bare exception type name (native failures surface as DdbException), JobIndex.ErrorMessage = the
            // reason a reader of the task may see (JobLogText.UserMessage: no server path). The full log
            // lives in the log tail persisted by BuildLogCaptureFilter. Synchronous filter ->
            // GetAwaiter().GetResult() like the observation refresh below.
            // Runs only when org/ds params are present (above early return): the writer
            // also busts the org/ds-scoped tasks-list cache. Errors stay inside the guard
            // so logging-path failures never break state transitions.
            var errorType = failedState.Exception?.GetType().Name ?? "Failed";
            var errorMessage = failedState.Exception is { } failure
                ? JobLogText.UserMessage(failure, DatasetFolderOf(context.BackgroundJob.Job, orgSlug, scope.ServiceProvider))
                : null;
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            writer.UpdateErrorAsync(jobId, errorType, errorMessage).GetAwaiter().GetResult();
            log.LogInformation(
                "BuildJobFailureFilter: job {JobId} ErrorType recorded as '{ErrorType}'",
                jobId, errorType);

            var pending = scope.ServiceProvider.GetRequiredService<BuildPendingService>();
            pending.RefreshPendingStatus(orgSlug, dsSlug, context.Storage).GetAwaiter().GetResult();
        });

    // The folder of the dataset the build job worked on, so its error names files relative to it; null when the job
    // does not say (JobLogText.UserMessage then removes every server path all the same)
    private string? DatasetFolderOf(Job job, string orgSlug, IServiceProvider services)
    {
        try
        {
            // DdbJobs: (orgSlug, internalRef, ...); the jobs of earlier releases: (IDDB ddb, ...)
            if (job.Args is [IDDB ddb, ..])
                return ddb.DatasetFolderPath;
            if (job.Args is [_, Guid internalRef, ..])
                return services.GetService<IDdbManager>()?.GetDatasetFolder(orgSlug, internalRef);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "BuildJobFailureFilter: cannot tell the dataset folder of a failed build");
        }

        return null;
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        // No-op: we only react to a state being applied.
    }
}
