using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// Default <see cref="IHeavyTaskRunner"/>: resolves a native tool, runs the submit checks of every tool
/// (<see cref="CheckSubmitAsync"/>) and enqueues the <see cref="HeavyTaskJobWrapper"/> on the <c>tasks</c> queue.
/// </summary>
public sealed class HeavyTaskRunner : IHeavyTaskRunner
{
    private readonly IHeavyToolRegistry _registry;
    private readonly IDatasetResolver _datasets;
    private readonly IDdbManager _ddbManager;
    private readonly HeavyTaskQuota _quota;
    private readonly HeavyTaskDedup _dedup;
    private readonly IJobIndexQuery _jobIndex;
    private readonly HeavyTaskSubmitLock _submitLock;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly ILogger<HeavyTaskRunner> _log;

    public HeavyTaskRunner(
        IHeavyToolRegistry registry,
        IDatasetResolver datasets,
        IDdbManager ddbManager,
        HeavyTaskQuota quota,
        HeavyTaskDedup dedup,
        IJobIndexQuery jobIndex,
        HeavyTaskSubmitLock submitLock,
        IBackgroundJobsProcessor processor,
        ILogger<HeavyTaskRunner> log)
    {
        _registry = registry;
        _datasets = datasets;
        _ddbManager = ddbManager;
        _quota = quota;
        _dedup = dedup;
        _jobIndex = jobIndex;
        _submitLock = submitLock;
        _processor = processor;
        _log = log;
    }

    /// <inheritdoc />
    public Task<HeavyTaskSubmitResult> SubmitAsync(HeavyTaskSubmitRequest request, CancellationToken ct = default) =>
        CheckSubmitAsync(request, (admission, _) => Task.FromResult(_processor.EnqueueHeavyTask(admission.Request,
            request.UserId, request.Hash, admission.RequestHash, request.ParentJobId, request.WorkflowExecutionId,
            admission.OutputPaths, admission.Label)), ct: ct);

    /// <inheritdoc />
    public async Task<HeavyTaskSubmitResult> CheckSubmitAsync(HeavyTaskSubmitRequest request,
        Func<HeavyTaskAdmission, CancellationToken, Task<string>> enqueue, string? ownJobId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(enqueue);

        var tool = _registry.Resolve(request.ToolId, request.Version)
                   ?? throw new HeavyToolNotFoundException($"Tool '{request.ToolId}' is not available.");

        // The callers authorized the request (dataset or organization write access) before submitting
        var ds = await _datasets.GetRequiredDatasetAsync(request.OrgSlug, request.DsSlug, ct: ct);

        var ddb = _ddbManager.Get(request.OrgSlug, ds.InternalRef);

        var toolRequest = new HeavyToolRequest(
            tool.Id, tool.Version, request.OrgSlug, request.DsSlug, request.Path, request.Params);

        // The retry of a task that already wrote its output (then failed to index it) does not refuse its own output
        // as an existing file: the validation sees what the earlier attempt wrote, as the job's next run does
        var published = ownJobId is null ? null : _processor.GetJobParameter(ownJobId, PublishedOutputMarker.Key);
        var validationCtx = new HeavyToolValidationContext(ddb, request.Caller, _log, published);

        // An output that is one of the inputs is a 400 (D-040). It is checked before the tool's validation,
        // which would refuse it as an existing file (409) that no overwrite can make valid. Parameters that are
        // not valid yet are reported by the validation.
        var declared = HeavyToolPaths.TryDeclared(tool, toolRequest);
        if (declared != null)
            HeavyToolPaths.EnsureOutputsAreNotInputs(declared.Inputs, declared.Outputs);

        await tool.ValidateAsync(toolRequest, validationCtx, ct);

        if (declared == null)
        {
            declared = HeavyToolPaths.Declared(tool, toolRequest);
            HeavyToolPaths.EnsureOutputsAreNotInputs(declared.Inputs, declared.Outputs);
        }

        var plan = tool.Plan(toolRequest, validationCtx);
        var requestHash = HeavyTaskRequestHash.Compute(tool.Id, tool.Version, request.Hash, request.Params);

        // Check-then-enqueue is atomic per dataset: two submits (a double click, two hosts) cannot both pass
        using var _ = _submitLock.Acquire(request.OrgSlug, request.DsSlug);

        // Dedup first: an equivalent task is the one that writes these outputs, so a second click gets its id.
        // Workflow sub-tasks always force (never reuse a pre-existing terminal task).
        if (!request.Force &&
            await FindDuplicateAsync(request, tool, requestHash, ct) is { } candidateId &&
            candidateId != ownJobId)
        {
            _log.LogInformation("Deduplicated task submit for tool {ToolId} -> {JobId}", tool.Id, candidateId);
            return new HeavyTaskSubmitResult(candidateId, tool.Id, tool.Version, true, plan.EstimatedOutputBytes);
        }

        await EnsureOutputsNotWrittenAsync(request.OrgSlug, request.DsSlug, declared.Outputs, ownJobId, ct);

        var quotaResult = await _quota.EvaluateAsync(request, plan, ct);
        if (!quotaResult.IsAllowed)
            throw new HeavyTaskQuotaException(quotaResult.Code, quotaResult.Message ?? "Quota exceeded.");

        var jobId = await enqueue(new HeavyTaskAdmission(tool, toolRequest, requestHash, declared.Outputs,
            declared.Label), ct);

        return new HeavyTaskSubmitResult(jobId, tool.Id, tool.Version, false, plan.EstimatedOutputBytes);
    }

    // The task an equivalent submit gets (null: none, or deduplication is off). A finished task is reused only for a
    // tool whose result is a downloadable artifact; a tool that writes the dataset reuses only an unfinished task,
    // since what a finished one wrote may have been deleted or replaced since (FR-PRC-02)
    private async Task<string?> FindDuplicateAsync(HeavyTaskSubmitRequest request, IHeavyTool tool, string requestHash,
        CancellationToken ct)
    {
        var candidateId = await _dedup.FindAsync(request.OrgSlug, request.DsSlug, tool.Id, requestHash, ct);
        if (candidateId is null || tool.ProducesArtifact)
            return candidateId;

        return (await _jobIndex.FindActiveDedupCandidateAsync(request.OrgSlug, request.DsSlug, tool.Id, requestHash, ct))
            ?.JobId;
    }

    // An output that an unfinished task of the dataset writes (its own record excluded): 409
    private async Task EnsureOutputsNotWrittenAsync(string orgSlug, string dsSlug, IReadOnlyList<string> outputs,
        string? ownJobId, CancellationToken ct)
    {
        if (outputs.Count == 0)
            return;

        foreach (var task in await _jobIndex.GetActiveOutputsAsync(orgSlug, dsSlug, ct))
        {
            if (task.JobId == ownJobId)
                continue;

            var written = task.Paths.ToHashSet(HeavyToolPaths.Comparer);
            var clash = outputs.FirstOrDefault(written.Contains);
            if (clash == null)
                continue;

            _log.LogInformation("Refused a {ToolId} submit: task {JobId} writes '{Path}'", task.ToolId, task.JobId, clash);
            throw new ConflictException(HeavyToolPaths.OutputInUseMessage(clash));
        }
    }
}
