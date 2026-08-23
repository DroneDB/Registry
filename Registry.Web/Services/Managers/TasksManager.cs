#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Processing Platform task substrate for a single dataset. Authorization combines
/// dataset access with per-task ownership.
/// </summary>
public sealed class TasksManager : ITasksManager
{
    private const int MaxTake = 200;

    private readonly IHeavyTaskRunner _runner;
    private readonly IHeavyToolRegistry _registry;
    private readonly IHeavyToolGating _gating;
    private readonly IJobIndexQuery _query;
    private readonly IJobIndexWriter _writer;
    private readonly IAuthManager _authManager;
    private readonly IUtils _utils;
    private readonly IBackgroundJobsProcessor _processor;
    private readonly IDdbManager _ddbManager;
    private readonly IDistributedCache _cache;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ProcessingPlatformSettings _settings;
    private readonly string _tempPath;
    private readonly ILogger<TasksManager> _logger;

    public TasksManager(
        IHeavyTaskRunner runner,
        IHeavyToolRegistry registry,
        IHeavyToolGating gating,
        IJobIndexQuery query,
        IJobIndexWriter writer,
        IAuthManager authManager,
        IUtils utils,
        IBackgroundJobsProcessor processor,
        IDdbManager ddbManager,
        IDistributedCache cache,
        IHttpContextAccessor httpContextAccessor,
        IOptions<AppSettings> appSettings,
        ILogger<TasksManager> logger)
    {
        _runner = runner;
        _registry = registry;
        _gating = gating;
        _query = query;
        _writer = writer;
        _authManager = authManager;
        _utils = utils;
        _processor = processor;
        _ddbManager = ddbManager;
        _cache = cache;
        _httpContextAccessor = httpContextAccessor;
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
        _tempPath = appSettings.Value.TempPath ?? Path.Combine(Path.GetTempPath(), "registry");
        _logger = logger;
    }

    public async Task<TaskToolDto[]> GetToolsAsync(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw AccessDenied();

        return await Task.WhenAll(_registry.All.Select(async t =>
        {
            var st = await _gating.EvaluateAsync(t.Id, orgSlug);
            return new TaskToolDto(t.Id, t.Version, t.Title,
                t.RequiredAccess.ToString(), t.ProducesArtifact, t.InputSchema.RootElement.Clone(),
                st.Hidden, st.Disabled, st.DisabledMessage);
        }));
    }

    public async Task<SubmitTaskResponseDto> SubmitAsync(string orgSlug, string dsSlug, SubmitTaskRequestDto body,
        CancellationToken ct = default)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ToolId))
            throw new BadRequestException("toolId is required");

        var tool = _registry.Resolve(body.ToolId, body.Version);
        if (tool is null)
            throw new BadRequestException($"Tool '{body.ToolId}' is not available");

        // Feature gating: reject hidden/disabled tools (or role/org-denied) server-side,
        // independent of the client UI.
        var gatingState = await _gating.EvaluateAsync(tool.Id, orgSlug);
        if (!gatingState.Allowed)
            throw new ForbiddenException(
                gatingState.DisabledMessage ?? $"Tool '{tool.Id}' is not available.", noRetry: true);

        var ds = _utils.GetDataset(orgSlug, dsSlug);
        var requiredAccess = tool.RequiredAccess == HeavyToolPermission.Write ? AccessType.Write : AccessType.Read;
        if (!await _authManager.RequestAccess(ds, requiredAccess))
            throw AccessDenied();

        var user = await _authManager.GetCurrentUser();

        // Convert JToken? (Newtonsoft) → JsonElement (System.Text.Json).
        // JsonElement is not bindable by Newtonsoft model binding; JToken? is.
        var paramsJson = body.Params?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
        var paramsElement = JsonSerializer.Deserialize<JsonElement>(paramsJson);

        var req = new HeavyTaskSubmitRequest(
            orgSlug, dsSlug, tool.Id, tool.Version, body.Path, paramsElement, body.Force,
            user?.Id, _httpContextAccessor.HttpContext?.User);

        var result = await _runner.SubmitAsync(req, ct);

        var baseUrl = $"/orgs/{orgSlug}/ds/{dsSlug}/tasks/{result.TaskId}";
        return new SubmitTaskResponseDto(
            result.TaskId, result.ToolId, result.Version, result.Deduplicated,
            baseUrl, baseUrl + "/result", result.EstimatedOutputBytes);
    }

    public async Task<TaskSummaryDto[]> ListAsync(string orgSlug, string dsSlug, string? toolId, string? state,
        int skip, int take, CancellationToken ct = default)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            throw AccessDenied();

        var clampedTake = Math.Clamp(take, 1, MaxTake);

        // Canonical shape (no filters, skip=0): serve from distributed cache.
        // Filtered queries bypass cache to avoid combinator explosion of cache keys.
        // Safety: the write-through invalidation in JobIndexWriter covers all mutations.
        var useCache = string.IsNullOrWhiteSpace(toolId) && string.IsNullOrWhiteSpace(state) && skip == 0;

        if (!useCache)
        {
            var filter = new JobIndexQueryFilter(orgSlug, dsSlug, toolId, state,
                Skip: Math.Max(0, skip), Take: clampedTake);
            var rows = await _query.QueryAsync(filter, ct);
            return [.. rows.Select(ToSummary)];
        }

        // The cached payload is always the canonical page (first MaxTake rows) so that a
        // request with a small take cannot poison later requests asking for more rows.
        var cacheKey = MagicStrings.TasksListCacheKey(orgSlug, dsSlug);
        var cachedBytes = await _cache.GetAsync(cacheKey, ct);

        TaskSummaryDto[] canonical;
        if (cachedBytes != null)
        {
            canonical = JsonSerializer.Deserialize<TaskSummaryDto[]>(cachedBytes) ?? [];
        }
        else
        {
            var filter = new JobIndexQueryFilter(orgSlug, dsSlug, toolId, state,
                Skip: 0, Take: MaxTake);
            var rows = await _query.QueryAsync(filter, ct);
            canonical = [.. rows.Select(ToSummary)];

            var options = new DistributedCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) };
            await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(canonical), options, ct);
        }

        return canonical.Length > clampedTake ? canonical[..clampedTake] : canonical;
    }

    public async Task<int> ClearAsync(string orgSlug, string dsSlug, string? toolId, CancellationToken ct = default)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            throw AccessDenied();

        // Permanently remove concluded (Succeeded/Failed/Deleted) tasks from the history
        // and purge any artifacts they produced, instead of merely flipping them to a
        // terminal "Deleted" state where they would linger in the list forever.
        var removedIds = await _writer.DeleteTerminalForDatasetAsync(orgSlug, dsSlug, toolId, ct);
        foreach (var jobId in removedIds)
            TaskJobHelpers.TryDeleteArtifacts(jobId, _tempPath, _logger);

        _logger.LogInformation("Cleared {Count} concluded task(s) for {Org}/{Ds}", removedIds.Count, orgSlug, dsSlug);
        return removedIds.Count;
    }

    public async Task<TaskStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string id,
        CancellationToken ct = default)
    {
        var job = await LoadAuthorizedTaskAsync(orgSlug, dsSlug, id, AccessType.Read, ct);

        var snapshot = LogTailSnapshot.Parse(job.LogTailJson);
        return new TaskStatusDto(
            job.JobId, job.ToolId, job.ToolVersion, job.CurrentState,
            new TaskProgressDto(job.ProgressPercent, job.PhaseMessage, null),
            job.CreatedAtUtc, job.ProcessingAtUtc, TaskJobHelpers.FinishedAt(job),
            job.ParentJobId, job.WorkflowExecutionId,
            snapshot.Cursor, snapshot.AsStrings(), BuildArtifactDto(job, orgSlug, dsSlug),
            job.ErrorType);
    }

    public async Task<TaskLogDto> GetLogAsync(string orgSlug, string dsSlug, string id, long since,
        CancellationToken ct = default)
    {
        var job = await LoadAuthorizedTaskAsync(orgSlug, dsSlug, id, AccessType.Read, ct);

        var snapshot = LogTailSnapshot.Parse(job.LogTailJson);
        var allLines = snapshot.AsStrings();

        // Return only lines beyond the caller's cursor. The ring buffer cursor is a
        // monotonic count; the available tail starts at (cursor - lines.Count).
        var tailStart = snapshot.Cursor - allLines.Count;
        var skip = since > tailStart ? (int)(since - tailStart) : 0;
        skip = Math.Clamp(skip, 0, allLines.Count);
        var lines = allLines.Skip(skip).ToArray();

        return new TaskLogDto(snapshot.Cursor, lines, snapshot.TruncatedFromTail);
    }

    public async Task<TaskArtifactFile> GetResultAsync(string orgSlug, string dsSlug, string id,
        CancellationToken ct = default)
    {
        var job = await LoadAuthorizedTaskAsync(orgSlug, dsSlug, id, AccessType.Read, ct);

        if (job.CurrentState != "Succeeded" || job.ArtifactSizeBytes is null)
            throw new NotFoundException("Task has no downloadable result");

        var file = TaskJobHelpers.ResolveArtifactFile(id, _tempPath);
        if (file is null)
            throw new NotFoundException("Artifact no longer available (expired or cleaned up)");

        var etag = string.IsNullOrEmpty(job.ArtifactSha256) ? null : $"\"{job.ArtifactSha256}\"";
        return new TaskArtifactFile(file, Path.GetFileName(file),
            MimeMapping.MimeUtility.GetMimeMapping(file), etag);
    }

    public async Task<bool> CancelAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await LoadAuthorizedTaskAsync(orgSlug, dsSlug, id, AccessType.Write, ct);
        return _processor.Delete(job.JobId);
    }

    public async Task<bool> RetryAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await LoadAuthorizedTaskAsync(orgSlug, dsSlug, id, AccessType.Write, ct);

        // Sweep-ownership rule: dependency-gated builds are re-run by the pending-build sweep;
        // the Retry button must not start a competing run while a .pending marker exists.
        if (job.ToolId == HeavyToolIds.Build && IsBuildPendingSafe(orgSlug, dsSlug))
            throw new ConflictException(
                "A pending build retry is already scheduled for this dataset; the pending-build sweep owns that retry");

        // Re-queue is gated by Hangfire's LIVE state (Failed-only) - authoritative, since it
        // reads the actual store rather than the lagging display-only JobIndex row. Wipe stale
        // prior-run state (error fields, timestamps, artifacts) only AFTER an accepted transition,
        // so a rejected retry (job purged/expired or state moved on) never destroys the still
        // valid failure diagnostics the user just asked about.
        var requeued = _processor.Requeue(job.JobId);
        if (requeued)
        {
            await _writer.ResetForRequeueAsync(job.JobId, ct);
            return true;
        }

        _logger.LogInformation(
            "Retry of task {JobId} rejected: row state '{State}' (Failed-only guard or job missing in Hangfire)",
            job.JobId, job.CurrentState);
        // State-free message on purpose: the DB row state can lag the guard's live read,
        // so do not assert a state here; details are in the log line above.
        throw new ConflictException(
            "Task cannot be retried in its current state or no longer exists in the job store");
    }

    public async Task<bool> DeleteAsync(string orgSlug, string dsSlug, string id, CancellationToken ct = default)
    {
        var job = await LoadAuthorizedTaskAsync(orgSlug, dsSlug, id, AccessType.Write, ct);

        var (deleted, _, _) = await _writer.DeleteTerminalJobByIdAsync(job.JobId, ct);
        if (!deleted)
            throw new BadRequestException(
                $"Task '{job.CurrentState}' cannot be deleted; only concluded tasks can be removed from history");

        // Best-effort artifact cleanup (same as Clear)
        TaskJobHelpers.TryDeleteArtifacts(job.JobId, _tempPath, _logger);

        _logger.LogInformation("Deleted terminal JobIndex record for job {JobId} in {Org}/{Ds}", job.JobId, orgSlug,
            dsSlug);
        return deleted;
    }

    /// <summary>
    /// Resolves a task after checking dataset access and per-task ownership.
    /// </summary>
    private async Task<JobIndex> LoadAuthorizedTaskAsync(
        string orgSlug, string dsSlug, string id, AccessType access, CancellationToken ct)
    {
        // A missing/unknown dataset surfaces as an unhandled exception; the global
        // classifier maps it to the same 404 the per-action build used to produce.
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, access))
            throw AccessDenied();

        var job = await _query.GetByJobIdAsync(orgSlug, dsSlug, id, ct)
                  ?? throw new NotFoundException("Task not found");

        // Ownership: owner, dataset owner/admin, or system task.
        var user = await _authManager.GetCurrentUser();
        var isOwner = job.UserId is null
                      || (user is not null && job.UserId == user.Id)
                      || await _authManager.IsOwnerOrAdmin(ds);
        if (!isOwner)
            throw new ForbiddenException("Forbidden");

        return job;
    }

    /// <summary>
    /// Checks the dataset for on-disk pending-build markers (the <c>.pending</c> files the
    /// DroneDB build rethrow leaves behind). Any failure returns false so that a ddb
    /// availability hiccup never blocks the user-initiated retry.
    /// </summary>
    private bool IsBuildPendingSafe(string orgSlug, string dsSlug)
    {
        try
        {
            var ds = _utils.GetDataset(orgSlug, dsSlug);
            var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
            return ddb.IsBuildPending();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retry: IsBuildPending check failed for {Org}/{Ds}, continuing retry", orgSlug,
                dsSlug);
            return false;
        }
    }

    private TaskSummaryDto ToSummary(JobIndex j) => new(
        j.JobId, j.ToolId, j.ToolVersion, j.CurrentState, j.ProgressPercent, j.PhaseMessage,
        j.CreatedAtUtc, j.ProcessingAtUtc, TaskJobHelpers.FinishedAt(j), j.Path, j.ParentJobId,
        j.WorkflowExecutionId, j.ErrorType, TaskJobHelpers.ArtifactExpiresAt(j, _settings.ArtifactTtlHours));

    private TaskArtifactDto? BuildArtifactDto(JobIndex j, string orgSlug, string dsSlug) =>
        j.CurrentState == "Succeeded" && j.ArtifactSizeBytes is { } size
            ? new TaskArtifactDto(size, j.ArtifactSha256, $"/orgs/{orgSlug}/ds/{dsSlug}/tasks/{j.JobId}/result",
                TaskJobHelpers.ArtifactExpiresAt(j, _settings.ArtifactTtlHours))
            : null;

    // 401 with noRetry:false, matching the response the controller produced before the manager split.
    private static UnauthorizedException AccessDenied() =>
        new("Access denied") { NoRetry = false };
}
