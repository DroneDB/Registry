using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.HeavyTasks.Adapters;

/// <summary>
/// What a validated submit meets under the dataset's submit lock, before it is queued (<see cref="HeavyTaskRunner"/>):
/// the equivalent task it gets instead (deduplication), an output that an unfinished task of the dataset writes (409),
/// for a tool that adds dataset files the storage of the owner of the target organization (507, FR-XC-26), and the task
/// quota. Submit and the retry of a failed task run them alike.
/// </summary>
public sealed class HeavyTaskAdmissionChecks
{
    private readonly HeavyTaskDedup _dedup;
    private readonly IJobIndexQuery _jobIndex;
    private readonly IStorageQuota _storage;
    private readonly HeavyTaskQuota _quota;
    private readonly ILogger<HeavyTaskAdmissionChecks> _log;

    public HeavyTaskAdmissionChecks(HeavyTaskDedup dedup, IJobIndexQuery jobIndex, IStorageQuota storage,
        HeavyTaskQuota quota, ILogger<HeavyTaskAdmissionChecks> log)
    {
        _dedup = dedup;
        _jobIndex = jobIndex;
        _storage = storage;
        _quota = quota;
        _log = log;
    }

    // The task an equivalent submit gets (null: none, or deduplication is off). A finished task is reused only for a
    // tool whose result is a downloadable artifact; a tool that writes the dataset reuses only an unfinished task,
    // since what a finished one wrote may have been deleted or replaced since (FR-PRC-02). The task of a tool that needs
    // only read access is authorized per user (its status, its result): a submit reuses only a task of the same user,
    // or for an anonymous one a task of no user, which every caller of the dataset may touch already (FR-RAS-11)
    public async Task<string?> FindDuplicateAsync(HeavyTaskSubmitRequest request, IHeavyTool tool, string requestHash,
        CancellationToken ct)
    {
        var ownedBy = tool.RequiredAccess == HeavyToolPermission.Read ? new TaskOwnerScope(request.UserId) : null;
        var candidateId = await _dedup.FindAsync(request.OrgSlug, request.DsSlug, tool.Id, requestHash, ownedBy, ct);
        if (candidateId is null || tool.ProducesArtifact)
            return candidateId;

        return (await _jobIndex.FindActiveDedupCandidateAsync(request.OrgSlug, request.DsSlug, tool.Id, requestHash,
            ownedBy, ct))?.JobId;
    }

    /// <summary>
    /// Refuses the submit when an unfinished task of the dataset (other than <paramref name="ownJobId"/>) writes one of
    /// <paramref name="outputs"/> (409), when the tool adds dataset files and the owner of the target organization has no
    /// room for them (507), or when the task quota is exceeded.
    /// </summary>
    public async Task EnsureAdmittedAsync(HeavyTaskSubmitRequest request, IHeavyTool tool, HeavyToolRequest toolRequest,
        HeavyToolPlan plan, IReadOnlyList<string> outputs, string? ownJobId, CancellationToken ct)
    {
        await EnsureOutputsNotWrittenAsync(request.OrgSlug, request.DsSlug, outputs, ownJobId, ct);

        if (tool.AddsDatasetFiles)
            await EnsureStorageAsync(tool, toolRequest, plan, ct);

        var quotaResult = await _quota.EvaluateAsync(request, plan, ct);
        if (!quotaResult.IsAllowed)
            throw new HeavyTaskQuotaException(quotaResult.Code, quotaResult.Message ?? "Quota exceeded.");
    }

    // The storage quota of the owner of the target organization, whoever submits (FR-XC-26, D-039): refused (507) when the
    // owner has nothing left, or when the tool's known output estimate does not fit (only its growth: the files it replaces
    // are counted in the owner's use already, HeavyToolPlan.ReplacedBytes). A tool without an estimate passes;
    // the worker reads the remaining bytes again when it writes.
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
