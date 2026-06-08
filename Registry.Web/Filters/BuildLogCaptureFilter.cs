#nullable enable
using System;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Ports;

namespace Registry.Web.Filters;

/// <summary>
/// Hangfire server filter that captures the console output of build/maintenance
/// jobs (BuildWrapper, BuildPendingWrapper, CleanupWrapper, MaskBordersWrapper)
/// into a per-job <see cref="LogRingBuffer"/> and persists it to
/// <c>JobIndex.LogTailJson</c> when the job finishes. This makes the Task
/// History "View log" dialog show build logs the same way it already shows
/// heavy-task logs (which persist their own tail via HangfireProgressSink).
/// </summary>
public sealed class BuildLogCaptureFilter(
    IServiceProvider sp,
    ILogger<BuildLogCaptureFilter> log) : IServerFilter
{
    /// <summary>Key under which the active job's log buffer is stored in <c>PerformContext.Items</c>.</summary>
    public const string BufferKey = "JobLogBuffer";

    private static readonly string[] CapturedMethods =
    [
        nameof(Utilities.HangfireUtils.BuildWrapper),
        nameof(Utilities.HangfireUtils.BuildPendingWrapper),
        nameof(Utilities.HangfireUtils.CleanupWrapper),
        nameof(Utilities.HangfireUtils.MaskBordersWrapper)
    ];

    public void OnPerforming(PerformingContext context)
    {
        var methodName = context.BackgroundJob?.Job?.Method?.Name;
        if (string.IsNullOrEmpty(methodName))
            return;

        if (Array.IndexOf(CapturedMethods, methodName) < 0)
            return;

        // The job retrieves this buffer from context.Items and appends each
        // console line to it; we flush it to the JobIndex when the job ends.
        context.Items[BufferKey] = new LogRingBuffer();
    }

    public void OnPerformed(PerformedContext context)
    {
        if (!context.Items.TryGetValue(BufferKey, out var raw) || raw is not LogRingBuffer buffer)
            return;

        try
        {
            var jobId = context.BackgroundJob?.Id;
            if (string.IsNullOrEmpty(jobId))
                return;

            string logTailJson;
            lock (buffer)
                logTailJson = buffer.ToJson();

            using var scope = sp.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IJobIndexWriter>();
            writer.UpdateProgressAsync(jobId, null, null, logTailJson, DateTime.UtcNow)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Logging is best-effort; never let it affect job completion.
            log.LogDebug(ex, "BuildLogCaptureFilter: failed to persist log tail for job {JobId}",
                context.BackgroundJob?.Id);
        }
    }
}
