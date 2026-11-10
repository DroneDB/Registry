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
    private readonly IStorageQuota _storage;
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
        IStorageQuota storage,
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
        _storage = storage;
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
            admission.OutputPaths, admission.Label, request.CreatedDataset, admission.Tool.AddsDatasetFiles)), ct: ct);

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
        // Likewise, it does not refuse the dataset an earlier run of it created (its write record) as a taken slug: the
        // re-run imports into that dataset again
        var ownCreated = ownJobId is null ? null : request.CreatedDataset;
        var validationCtx = new HeavyToolValidationContext(ddb, request.Caller, _log, published, ownCreated);

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

        if (tool.AddsDatasetFiles)
            await EnsureStorageAsync(tool, toolRequest, plan, ct);

        var quotaResult = await _quota.EvaluateAsync(request, plan, ct);
        if (!quotaResult.IsAllowed)
            throw new HeavyTaskQuotaException(quotaResult.Code, quotaResult.Message ?? "Quota exceeded.");

        var jobId = await enqueue(new HeavyTaskAdmission(tool, toolRequest, requestHash, declared.Outputs,
            declared.Label), ct);

        return new HeavyTaskSubmitResult(jobId, tool.Id, tool.Version, false, plan.EstimatedOutputBytes);
    }

    // The storage quota of the owner of the target organization, whoever submits (FR-XC-26, D-039): refused (507) when the
    // owner has nothing left, or when the tool's known output estimate does not fit (only its growth: the files it replaces
    // are counted in the owner's use already, HeavyToolPlan.ReplacedBytes). A tool without an estimate passes;
    // the worker reads the remaining bytes again when it writes. Submit and the retry of a failed task run it alike.
    private async Task EnsureStorageAsync(IHeavyTool tool, HeavyToolRequest request, HeavyToolPlan plan,
        CancellationToken ct)
    {
        var remaining = await _storage.RemainingBytesOfOrganizationAsync(tool.StorageOrganization(request), ct);
        if (remaining is not { } left)
            return;

        if (left <= 0 || plan.StorageGrowthBytes > left)
        {
            _log.LogInformation("Refused a {ToolId} submit: the owner of the organization has {Remaining} bytes left",
                tool.Id, left);
            throw QuotaExceededException.OwnerOverQuota(left);
        }
    }

    // The task an equivalent submit gets (null: none, or deduplication is off). A finished task is reused only for a
    // tool whose result is a downloadable artifact; a tool that writes the dataset reuses only an unfinished task,
    // since what a finished one wrote may have been deleted or replaced since (FR-PRC-02). The task of a tool that needs
    // only read access is authorized per user (its status, its result): a submit reuses only a task of the same user,
    // or for an anonymous one a task of no user, which every caller of the dataset may touch already (FR-RAS-11)
    private async Task<string?> FindDuplicateAsync(HeavyTaskSubmitRequest request, IHeavyTool tool, string requestHash,
        CancellationToken ct)
    {
        var ownedBy = tool.RequiredAccess == HeavyToolPermission.Read ? new TaskOwnerScope(request.UserId) : null;
        var candidateId = await _dedup.FindAsync(request.OrgSlug, request.DsSlug, tool.Id, requestHash, ownedBy, ct);
        if (candidateId is null || tool.ProducesArtifact)
            return candidateId;

        return (await _jobIndex.FindActiveDedupCandidateAsync(request.OrgSlug, request.DsSlug, tool.Id, requestHash,
            ownedBy, ct))?.JobId;
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
