#nullable enable
using System;
using Hangfire.Console;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.Adapters;

namespace Registry.Web.Services.Jobs;

/// <summary>
/// Gives every job a libddb operation (<see cref="JobOperations"/>, XI-08) for as long as it
/// runs, so the native log records of its calls carry the job id and dataset; writes the native
/// warnings still queued when the job ends to its console. The operation flows into the job from
/// <see cref="OnPerforming"/> (synchronous, same flow as the job).
/// </summary>
public sealed class JobOperationFilter(ILogger<JobOperationFilter> log) : IServerFilter
{
    private const string ItemKey = "DdbJobOperation";

    public void OnPerforming(PerformingContext context)
    {
        try
        {
            var jobId = context.BackgroundJob.Id;
            // Raw reads, as the other filters do: IndexedJobEnqueuer stores the slugs as plain strings
            var orgSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.OrgSlug);
            var dsSlug = context.Connection.GetJobParameter(jobId, JobParamKeys.DsSlug);
            var dataset = string.IsNullOrWhiteSpace(orgSlug) || string.IsNullOrWhiteSpace(dsSlug)
                ? null
                : $"{orgSlug}/{dsSlug}";

            context.Items[ItemKey] = JobOperations.Begin(jobId, dataset);
        }
        catch (Exception ex)
        {
            // A log annotation is never a reason to fail the job
            log.LogDebug(ex, "Cannot start the libddb operation of job {JobId}", context.BackgroundJob?.Id);
        }
    }

    public void OnPerformed(PerformedContext context)
    {
        if (!context.Items.TryGetValue(ItemKey, out var raw) || raw is not JobOperation operation)
            return;

        try
        {
            foreach (var line in operation.TakeNativeLines())
                context.WriteLine(line);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Cannot write the native messages of job {JobId} to its console", operation.JobId);
        }
        finally
        {
            operation.Dispose();
        }
    }
}
