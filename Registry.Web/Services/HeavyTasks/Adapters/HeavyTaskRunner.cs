#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Default <see cref="IHeavyTaskRunner"/>: resolves a native tool, validates and
/// plans the request, deduplicates, enforces quota and enqueues the
/// <see cref="HeavyTaskJobWrapper"/> on the <c>tasks</c> queue.
/// </summary>
public sealed class HeavyTaskRunner : IHeavyTaskRunner
{
    private readonly IHeavyToolRegistry _registry;
    private readonly IDatasetResolver _datasets;
    private readonly IDdbManager _ddbManager;
    private readonly IHeavyTaskQuota _quota;
    private readonly IHeavyTaskDedup _dedup;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly ILogger<HeavyTaskRunner> _log;

    public HeavyTaskRunner(
        IHeavyToolRegistry registry,
        IDatasetResolver datasets,
        IDdbManager ddbManager,
        IHeavyTaskQuota quota,
        IHeavyTaskDedup dedup,
        IBackgroundJobsProcessor processor,
        ILogger<HeavyTaskRunner> log)
    {
        _registry = registry;
        _datasets = datasets;
        _ddbManager = ddbManager;
        _quota = quota;
        _dedup = dedup;
        _processor = processor;
        _log = log;
    }

    public async Task<HeavyTaskSubmitResult> SubmitAsync(HeavyTaskSubmitRequest request, CancellationToken ct = default)
    {
        var tool = _registry.Resolve(request.ToolId, request.Version)
                   ?? throw new HeavyToolNotFoundException($"Tool '{request.ToolId}' is not available.");

        // The callers authorized the request (dataset or organization write access) before submitting
        var ds = await _datasets.GetDatasetAsync(request.OrgSlug, request.DsSlug, ct: ct);

        var ddb = _ddbManager.Get(request.OrgSlug, ds.InternalRef);

        var toolRequest = new HeavyToolRequest(
            tool.Id, tool.Version, request.OrgSlug, request.DsSlug, request.Path, request.Params);

        var validationCtx = new HeavyToolValidationContext(ddb, request.Caller, _log);

        await tool.ValidateAsync(toolRequest, validationCtx, ct);
        var plan = tool.Plan(toolRequest, validationCtx);

        var requestHash = HeavyTaskRequestHash.Compute(tool.Id, tool.Version, request.Hash, request.Params);

        // Dedup: workflow sub-tasks always force (never reuse a pre-existing terminal task).
        if (!request.Force &&
            await _dedup.FindAsync(request.OrgSlug, request.DsSlug, tool.Id, requestHash, ct) is { } candidateId)
        {
            _log.LogInformation("Deduplicated task submit for tool {ToolId} -> {JobId}", tool.Id, candidateId);
            return new HeavyTaskSubmitResult(candidateId, tool.Id, tool.Version, true, plan.EstimatedOutputBytes);
        }

        var quotaResult = await _quota.EvaluateAsync(request, plan, ct);
        if (!quotaResult.IsAllowed)
            throw new HeavyTaskQuotaException(quotaResult.Code, quotaResult.Message ?? "Quota exceeded.");

        var jobId = _processor.EnqueueHeavyTask(toolRequest, request.UserId, request.Hash, requestHash,
            request.ParentJobId, request.WorkflowExecutionId);

        return new HeavyTaskSubmitResult(jobId, tool.Id, tool.Version, false, plan.EstimatedOutputBytes);
    }
}
