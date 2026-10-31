using System.Text.Json;
using System.Threading;
using Registry.Web.Models;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>The one place that turns a heavy-tool request into a <see cref="HeavyTaskJobWrapper"/> job.</summary>
public static class HeavyTaskJobs
{
    /// <summary>
    /// Enqueues <paramref name="request"/> as an indexed job on the <see cref="JobQueues.Tasks"/>
    /// queue (<see cref="BuildQueueRoutingFilter"/> moves tools that run native builds to the
    /// builds queue).
    /// </summary>
    /// <param name="userId">The user the task is attributed to, or null for the system.</param>
    /// <param name="entryHash">Hash of the entry the task works on, if any.</param>
    /// <param name="requestHash">See <see cref="HeavyTaskRequestHash"/>; null skips deduplication.</param>
    /// <returns>The task (Hangfire job) id.</returns>
    public static string EnqueueHeavyTask(this IBackgroundJobsProcessor processor, HeavyToolRequest request,
        string? userId, string? entryHash = null, string? requestHash = null,
        string? parentJobId = null, string? workflowExecutionId = null)
    {
        var requestJson = JsonSerializer.Serialize(request);
        var toolId = request.ToolId;
        var version = request.Version;

        var payload = new IndexPayload(
            request.OrgSlug, request.DsSlug, entryHash, userId,
            Queue: JobQueues.Tasks, Path: request.Path,
            ToolId: toolId, ToolVersion: version,
            RequestHash: requestHash,
            ParentJobId: parentJobId,
            WorkflowExecutionId: workflowExecutionId);

        return processor.EnqueueIndexed<HeavyTaskJobWrapper>(
            w => w.Run(toolId, version, requestJson, null!, CancellationToken.None), payload);
    }
}
