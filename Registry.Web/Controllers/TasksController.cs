#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.HeavyTasks.Models;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Ports;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Controllers;

/// <summary>
/// Processing Platform task substrate REST surface. All routes live
/// under <c>/orgs/{org}/ds/{ds}/tasks</c>. Authorization combines dataset access
/// with per-task ownership.
/// </summary>
[ApiController]
[Route(RoutesHelper.OrganizationsRadix + "/" + RoutesHelper.OrganizationSlug + "/" + RoutesHelper.DatasetRadix +
       "/" + RoutesHelper.DatasetSlug + "/tasks")]
[Produces("application/json")]
public class TasksController : ControllerBaseEx
{
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
    private readonly ProcessingPlatformSettings _settings;
    private readonly string _tempPath;
    private readonly ILogger<TasksController> _logger;

    public TasksController(
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
        IOptions<AppSettings> appSettings,
        ILogger<TasksController> logger)
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
        _settings = appSettings.Value.ProcessingPlatform ?? new ProcessingPlatformSettings();
        _tempPath = appSettings.Value.TempPath ?? Path.Combine(Path.GetTempPath(), "registry");
        _logger = logger;
    }

    // ---- GET /tasks/tools -------------------------------------------------

    [HttpGet("tools", Name = nameof(TasksController) + "." + nameof(GetTools))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTools([FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            return Unauthorized(new ErrorResponse("Access denied"));

        var tools = await Task.WhenAll(_registry.All.Select(async t =>
        {
            var st = await _gating.EvaluateAsync(t.Id, orgSlug);
            return new TaskToolDto(t.Id, t.Version, t.Title,
                t.RequiredAccess.ToString(), t.ProducesArtifact, t.InputSchema.RootElement.Clone(),
                st.Hidden, st.Disabled, st.DisabledMessage);
        }));

        return Ok(tools);
    }

    // ---- POST /tasks ------------------------------------------------------

    [HttpPost(Name = nameof(TasksController) + "." + nameof(Submit))]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Submit(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromBody] SubmitTaskRequestDto body, CancellationToken ct)
    {
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ToolId))
                return BadRequest(new ErrorResponse("toolId is required"));

            var tool = _registry.Resolve(body.ToolId, body.Version);
            if (tool is null)
                return BadRequest(new ErrorResponse($"Tool '{body.ToolId}' is not available"));

            // Feature gating: reject hidden/disabled tools (or role/org-denied) server-side,
            // independent of the client UI. Returns 403 with the configured message.
            var gatingState = await _gating.EvaluateAsync(tool.Id, orgSlug);
            if (!gatingState.Allowed)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new ErrorResponse(
                        gatingState.DisabledMessage ?? $"Tool '{tool.Id}' is not available.",
                        noRetry: true));

            var ds = _utils.GetDataset(orgSlug, dsSlug);
            var requiredAccess = tool.RequiredAccess == HeavyToolPermission.Write ? AccessType.Write : AccessType.Read;
            if (!await _authManager.RequestAccess(ds, requiredAccess))
                return Unauthorized(new ErrorResponse("Access denied"));

            var user = await _authManager.GetCurrentUser();

            // Convert JToken? (Newtonsoft) → JsonElement (System.Text.Json).
            // JsonElement is not bindable by Newtonsoft model binding; JToken? is.
            var paramsJson = body.Params?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";
            var paramsElement = JsonSerializer.Deserialize<JsonElement>(paramsJson);

            var req = new HeavyTaskSubmitRequest(
                orgSlug, dsSlug, tool.Id, tool.Version, body.Path, paramsElement, body.Force,
                user?.Id, User);

            var result = await _runner.SubmitAsync(req, ct);

            var baseUrl = $"/orgs/{orgSlug}/ds/{dsSlug}/tasks/{result.TaskId}";
            var response = new SubmitTaskResponseDto(
                result.TaskId, result.ToolId, result.Version, result.Deduplicated,
                baseUrl, baseUrl + "/result", result.EstimatedOutputBytes);

            // Dedup hit returns 200; fresh enqueue returns 202.
            return result.Deduplicated ? Ok(response) : Accepted(baseUrl, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task submit failed for tool '{ToolId}' on {OrgSlug}/{DsSlug}", body?.ToolId, orgSlug, dsSlug);
            throw;
        }
    }

    // ---- GET /tasks -------------------------------------------------------

    [HttpGet(Name = nameof(TasksController) + "." + nameof(List))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromQuery] string? toolId, [FromQuery] string? state,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Read))
            return Unauthorized(new ErrorResponse("Access denied"));

        const int maxTake = 200;
        var clampedTake = Math.Clamp(take, 1, maxTake);

        // Canonical shape (no filters, skip=0): serve from distributed cache.
        // Filtered queries bypass cache to avoid combinator explosion of cache keys.
        // Safety: the write-through invalidation in JobIndexWriter covers all mutations.
        bool useCache = string.IsNullOrWhiteSpace(toolId) && string.IsNullOrWhiteSpace(state) && skip == 0;

        TaskSummaryDto[] dtos;
        if (useCache)
        {
            // The cached payload is always the canonical page (first maxTake rows) so that a
            // request with a small take cannot poison later requests asking for more rows.
            var cacheKey = MagicStrings.TasksListCacheSeed + ":" + orgSlug + "/" + dsSlug;
            var cachedBytes = await _cache.GetAsync(cacheKey, ct);

            TaskSummaryDto[] canonical;
            if (cachedBytes != null)
            {
                canonical = JsonSerializer.Deserialize<TaskSummaryDto[]>(cachedBytes) ?? [];
            }
            else
            {
                var filter = new JobIndexQueryFilter(orgSlug, dsSlug, toolId, state,
                    Skip: 0, Take: maxTake);
                var rows = await _query.QueryAsync(filter, ct);
                canonical = rows.Select(ToSummary).ToArray();

                var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) };
                await _cache.SetAsync(cacheKey, JsonSerializer.SerializeToUtf8Bytes(canonical), options, ct);
            }

            dtos = canonical.Length > clampedTake ? canonical[..clampedTake] : canonical;
        }
        else
        {
            var filter = new JobIndexQueryFilter(orgSlug, dsSlug, toolId, state,
                Skip: Math.Max(0, skip), Take: clampedTake);
            var rows = await _query.QueryAsync(filter, ct);
            dtos = rows.Select(ToSummary).ToArray();
        }

        return Ok(dtos);
    }

    // ---- POST /tasks/clear ------------------------------------------------

    [HttpPost("clear", Name = nameof(TasksController) + "." + nameof(Clear))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Clear(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromQuery] string? toolId, CancellationToken ct)
    {
        var ds = _utils.GetDataset(orgSlug, dsSlug);
        if (!await _authManager.RequestAccess(ds, AccessType.Write))
            return Unauthorized(new ErrorResponse("Access denied"));

        // Permanently remove concluded (Succeeded/Failed/Deleted) tasks from the history
        // and purge any artifacts they produced, instead of merely flipping them to a
        // terminal "Deleted" state where they would linger in the list forever.
        var removedIds = await _writer.DeleteTerminalForDatasetAsync(orgSlug, dsSlug, toolId, ct);
        foreach (var jobId in removedIds)
            TryDeleteArtifacts(jobId);

        _logger.LogInformation("Cleared {Count} concluded task(s) for {Org}/{Ds}", removedIds.Count, orgSlug, dsSlug);
        return Ok(new { cleared = removedIds.Count });
    }

    // ---- GET /tasks/{id} --------------------------------------------------

    [HttpGet("{id}", Name = nameof(TasksController) + "." + nameof(GetStatus))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var (job, error) = await LoadAuthorizedTask(orgSlug, dsSlug, id, AccessType.Read, ct);
        if (error is not null) return error;

        var snapshot = ParseLogTail(job!.LogTailJson);
        return Ok(new TaskStatusDto(
            job.JobId, job.ToolId, job.ToolVersion, job.CurrentState,
            new TaskProgressDto(job.ProgressPercent, job.PhaseMessage, null),
            job.CreatedAtUtc, job.ProcessingAtUtc, FinishedAt(job),
            job.ParentJobId, job.WorkflowExecutionId,
            snapshot.Cursor, snapshot.AsStrings(), BuildArtifactDto(job, orgSlug, dsSlug),
            job.ErrorType));
    }

    // ---- GET /tasks/{id}/log ----------------------------------------------

    [HttpGet("{id}/log", Name = nameof(TasksController) + "." + nameof(GetLog))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLog(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, [FromQuery] long since = 0, CancellationToken ct = default)
    {
        var (job, error) = await LoadAuthorizedTask(orgSlug, dsSlug, id, AccessType.Read, ct);
        if (error is not null) return error;

        var snapshot = ParseLogTail(job!.LogTailJson);
        var allLines = snapshot.AsStrings();

        // Return only lines beyond the caller's cursor. The ring buffer cursor is a
        // monotonic count; the available tail starts at (cursor - lines.Count).
        var tailStart = snapshot.Cursor - allLines.Count;
        var skip = since > tailStart ? (int)(since - tailStart) : 0;
        skip = Math.Clamp(skip, 0, allLines.Count);
        var lines = allLines.Skip(skip).ToArray();

        return Ok(new TaskLogDto(snapshot.Cursor, lines, snapshot.TruncatedFromTail));
    }

    // ---- GET /tasks/{id}/result -------------------------------------------

    [HttpGet("{id}/result", Name = nameof(TasksController) + "." + nameof(GetResult))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetResult(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var (job, error) = await LoadAuthorizedTask(orgSlug, dsSlug, id, AccessType.Read, ct);
        if (error is not null) return error;

        if (job!.CurrentState != "Succeeded" || job.ArtifactSizeBytes is null)
            return NotFound(new ErrorResponse("Task has no downloadable result"));

        var file = ResolveArtifactFile(id);
        if (file is null)
            return NotFound(new ErrorResponse("Artifact no longer available (expired or cleaned up)"));

        if (!string.IsNullOrEmpty(job.ArtifactSha256))
            Response.Headers.ETag = $"\"{job.ArtifactSha256}\"";

        var contentType = MimeMapping.MimeUtility.GetMimeMapping(file);
        var stream = System.IO.File.OpenRead(file);
        return File(stream, contentType, Path.GetFileName(file), enableRangeProcessing: true);
    }

    // ---- DELETE /tasks/{id} -----------------------------------------------

    [HttpDelete("{id}", Name = nameof(TasksController) + "." + nameof(Cancel))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var (job, error) = await LoadAuthorizedTask(orgSlug, dsSlug, id, AccessType.Write, ct);
        if (error is not null) return error;

        var deleted = _processor.Delete(job!.JobId);
        return Ok(new { deleted });
    }

    // ---- POST /tasks/{id}/retry -------------------------------------------

    // Build tool identifier (matches the JobIndex.ToolId default and BuildStatusService.BuildToolId).
    private const string BuildToolId = "build";

    [HttpPost("{id}/retry", Name = nameof(TasksController) + "." + nameof(Retry))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retry(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var (job, error) = await LoadAuthorizedTask(orgSlug, dsSlug, id, AccessType.Write, ct);
        if (error is not null) return error;

        // Sweep-ownership rule: dependency-gated builds are re-run by the pending-build sweep;
        // the Retry button must not start a competing run while a .pending marker exists.
        if (job!.ToolId == BuildToolId && IsBuildPendingSafe(orgSlug, dsSlug))
            return Conflict(new ErrorResponse(
                "A pending build retry is already scheduled for this dataset; the pending-build sweep owns that retry"));

        // Reset stale prior-run state (error fields, timestamps, artifacts) while the row is
        // still Failed, BEFORE the re-queue, so it cannot race the async Enqueued stamp.
        await _writer.ResetForRequeueAsync(job.JobId, ct);

        var requeued = _processor.Requeue(job.JobId);
        if (!requeued)
        {
            _logger.LogInformation(
                "Retry of task {JobId} rejected: row state '{State}' (Failed-only guard or job missing in Hangfire)",
                job.JobId, job.CurrentState);
            // State-free message on purpose: the DB row state can lag the guard's live read,
            // so do not assert a state here; details are in the log line above.
            return Conflict(new ErrorResponse(
                "Task cannot be retried in its current state or no longer exists in the job store"));
        }
        return Ok(new { requeued });
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
            _logger.LogWarning(ex, "Retry: IsBuildPending check failed for {Org}/{Ds}, continuing retry", orgSlug, dsSlug);
            return false;
        }
    }

    // ---- POST /tasks/{id}/delete -----------------------------------------

    [HttpPost("{id}/delete", Name = nameof(TasksController) + "." + nameof(Delete))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute, Required] string orgSlug, [FromRoute, Required] string dsSlug,
        [FromRoute, Required] string id, CancellationToken ct)
    {
        var (job, error) = await LoadAuthorizedTask(orgSlug, dsSlug, id, AccessType.Write, ct);
        if (error is not null) return error;

        var (deleted, _, _) = await _writer.DeleteTerminalJobByIdAsync(job!.JobId, ct);
        if (!deleted)
            return BadRequest(new ErrorResponse($"Task '{job.CurrentState}' cannot be deleted; only concluded tasks can be removed from history"));

        // Best-effort artifact cleanup (same as Clear endpoint)
        TryDeleteArtifacts(job.JobId);

        _logger.LogInformation("Deleted terminal JobIndex record for job {JobId} in {Org}/{Ds}", job.JobId, orgSlug, dsSlug);
        return Ok(new { deleted });
    }

    // ---- helpers ----------------------------------------------------------

    private async Task<(JobIndex? job, IActionResult? error)> LoadAuthorizedTask(
        string orgSlug, string dsSlug, string id, AccessType access, CancellationToken ct)
    {
        // A missing/unknown dataset surfaces as the action's unhandled exception; the global
        // classifier maps it to the same 404 the per-action build used to produce before the unification.
        var ds = _utils.GetDataset(orgSlug, dsSlug);

        if (!await _authManager.RequestAccess(ds, access))
            return (null, Unauthorized(new ErrorResponse("Access denied")));

        var rows = await _query.QueryAsync(new JobIndexQueryFilter(orgSlug, dsSlug, Take: 1000), ct);
        var job = rows.FirstOrDefault(r => r.JobId == id);
        if (job is null)
            return (null, NotFound(new ErrorResponse("Task not found")));

        // Ownership: owner, dataset owner/admin, or system task.
        var user = await _authManager.GetCurrentUser();
        var isOwner = job.UserId is null
                      || (user is not null && job.UserId == user.Id)
                      || await _authManager.IsOwnerOrAdmin(ds);
        if (!isOwner)
            return (null, StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse("Forbidden")));

        return (job, null);
    }

    private TaskSummaryDto ToSummary(JobIndex j) => new(
        j.JobId, j.ToolId, j.ToolVersion, j.CurrentState, j.ProgressPercent, j.PhaseMessage,
        j.CreatedAtUtc, j.ProcessingAtUtc, FinishedAt(j), j.Path, j.ParentJobId, j.WorkflowExecutionId, j.ErrorType,
        ArtifactExpiresAt(j));

    private static DateTime? FinishedAt(JobIndex j) =>
        j.SucceededAtUtc ?? j.FailedAtUtc ?? j.DeletedAtUtc;

    /// <summary>
    /// Server-authoritative expiry of a produced artifact: the work directory is
    /// swept <c>ArtifactTtlHours</c> after completion (see <c>HeavyTaskJobWrapper</c>).
    /// Null when the task has no downloadable artifact. Clients hide the download
    /// control once this instant passes so they never offer a 404 link.
    /// </summary>
    private DateTime? ArtifactExpiresAt(JobIndex j) =>
        j.CurrentState == "Succeeded" && j.ArtifactSizeBytes is not null && j.SucceededAtUtc is { } finished
            ? finished.AddHours(Math.Max(1, _settings.ArtifactTtlHours))
            : null;

    private TaskArtifactDto? BuildArtifactDto(JobIndex j, string orgSlug, string dsSlug) =>
        j.CurrentState == "Succeeded" && j.ArtifactSizeBytes is { } size
            ? new TaskArtifactDto(size, j.ArtifactSha256, $"/orgs/{orgSlug}/ds/{dsSlug}/tasks/{j.JobId}/result",
                ArtifactExpiresAt(j))
            : null;

    private static LogTailSnapshot ParseLogTail(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new LogTailSnapshot();
        try
        {
            var snap = JsonSerializer.Deserialize<LogTailSnapshot>(json);
            return snap ?? new LogTailSnapshot();
        }
        catch
        {
            return new LogTailSnapshot();
        }
    }

    private string? ResolveArtifactFile(string taskId)
    {
        var dir = Path.Combine(_tempPath, "tasks", taskId);
        var full = Path.GetFullPath(dir);
        var root = Path.GetFullPath(Path.Combine(_tempPath, "tasks"));
        if (!full.StartsWith(root, StringComparison.Ordinal) || !Directory.Exists(full))
            return null;

        return Directory.EnumerateFiles(full).FirstOrDefault();
    }

    /// <summary>
    /// Best-effort removal of a task's produced-artifact working directory
    /// (<c>{tempPath}/tasks/{taskId}</c>). Path-guarded against traversal exactly like
    /// <see cref="ResolveArtifactFile"/>; failures are logged but never abort the clear.
    /// </summary>
    private void TryDeleteArtifacts(string taskId)
    {
        try
        {
            var dir = Path.GetFullPath(Path.Combine(_tempPath, "tasks", taskId));
            var root = Path.GetFullPath(Path.Combine(_tempPath, "tasks"));
            if (dir.StartsWith(root, StringComparison.Ordinal) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete artifacts for task {TaskId}", taskId);
        }
    }
}

internal static class LogTailSnapshotExtensions
{
    public static IReadOnlyList<string> AsStrings(this LogTailSnapshot snap) =>
        snap.Lines.Select(l => $"[{DateTimeOffset.FromUnixTimeMilliseconds(l.T):HH:mm:ss}] {l.Msg}").ToArray();
}
