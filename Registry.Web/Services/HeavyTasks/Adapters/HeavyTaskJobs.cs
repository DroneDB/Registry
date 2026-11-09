using System.Collections.Generic;
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
    /// <param name="outputPaths">The files the task writes (<see cref="Ports.IHeavyTool.OutputPaths"/>), stored in the task record.</param>
    /// <param name="targetLabel">The label of the task's target files (<see cref="Ports.IHeavyTool.Label"/>), stored in the task record.</param>
    /// <returns>The task (Hangfire job) id.</returns>
    public static string EnqueueHeavyTask(this IBackgroundJobsProcessor processor, HeavyToolRequest request,
        string? userId, string? entryHash = null, string? requestHash = null,
        string? parentJobId = null, string? workflowExecutionId = null,
        IReadOnlyList<string>? outputPaths = null, string? targetLabel = null)
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
            WorkflowExecutionId: workflowExecutionId,
            OutputPathsJson: OutputPathsJson(outputPaths),
            TargetLabel: FitLabel(targetLabel));

        return processor.EnqueueIndexed<HeavyTaskJobWrapper>(
            w => w.Run(toolId, version, requestJson, null!, CancellationToken.None), payload);
    }

    /// <summary>Longest label the task record keeps (the <c>TargetLabel</c> column, <c>JobIndexConfiguration</c>).</summary>
    public const int TargetLabelMaxLength = 256;

    // The column value of the output paths: a JSON array, or null when the tool declares none
    private static string? OutputPathsJson(IReadOnlyList<string>? outputPaths) =>
        outputPaths is { Count: > 0 } ? JsonSerializer.Serialize(outputPaths) : null;

    // A label longer than the column ends with an ellipsis instead of failing the insert
    private static string? FitLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;
        return label.Length <= TargetLabelMaxLength ? label : label[..(TargetLabelMaxLength - 1)] + "…";
    }
}
