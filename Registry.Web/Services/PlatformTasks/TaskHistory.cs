using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.States;
using Microsoft.Extensions.Options;
using Registry.Web.Services.Managers;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Caching;
using Registry.Web.Services.HeavyTasks.Adapters;
using Registry.Web.Services.Jobs;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.PlatformTasks;

/// <summary>What a dataset's tasks did: the list, a task's status, its log and its result (out of <c>TasksManager</c>).</summary>
public sealed class TaskHistory
{
    private const int MaxTake = 200;

    // The canonical first page lives a minute: JobIndexWriter removes it on every change of the dataset's tasks
    private static readonly RecordCacheOptions TasksListCacheOptions = RecordCacheOptions.For(TimeSpan.FromSeconds(60));

    private readonly IDatasetAccessResolver _access;
    private readonly IJobIndexQuery _query;
    private readonly IRecordCache _cache;
    private readonly TaskAuthorizer _authorizer;
    private readonly TaskArtifactStore _artifacts;
    private readonly BuildTaskWaits _waits;
    private readonly ProcessingPlatformSettings _settings;
    private readonly TaskUserNames? _userNames;

    public TaskHistory(IDatasetAccessResolver access, IJobIndexQuery query, IRecordCache cache,
        TaskAuthorizer authorizer, TaskArtifactStore artifacts, BuildTaskWaits waits,
        IOptions<ProcessingPlatformSettings> platformSettings, TaskUserNames? userNames = null)
    {
        _access = access;
        _query = query;
        _cache = cache;
        _authorizer = authorizer;
        _artifacts = artifacts;
        _waits = waits;
        _settings = platformSettings.Value;
        _userNames = userNames;
    }

    /// <summary>The page of the dataset's task history, newest first, with the entity tag of the page.</summary>
    public async Task<TaskListPage> ListPageAsync(string orgSlug, string dsSlug, string? toolId, string? state,
        int skip, int take, CancellationToken ct = default)
    {
        var dataset = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        // The actions of a row are the caller's (D-024): they are added after the cache, which every reader of the
        // dataset shares, and the entity tag is that of the page the caller gets
        var caller = await _authorizer.CallerAsync(dataset.Dataset);

        var clampedTake = Math.Clamp(take, 1, MaxTake);

        // Canonical shape (no filters, skip=0): serve from distributed cache.
        // Filtered queries bypass cache to avoid combinator explosion of cache keys.
        // Safety: the write-through invalidation in JobIndexWriter covers all mutations.
        var useCache = string.IsNullOrWhiteSpace(toolId) && string.IsNullOrWhiteSpace(state) && skip == 0;

        if (!useCache)
        {
            var filter = new JobIndexQueryFilter(orgSlug, dsSlug, toolId, state,
                Skip: Math.Max(0, skip), Take: clampedTake);
            var page = ForCaller(await ListedAsync(await _query.QueryAsync(filter, ct), dataset, ct), caller);
            return new TaskListPage(page, ETagOf(JsonSerializer.SerializeToUtf8Bytes(page), clampedTake));
        }

        // The cached payload is always the canonical page (first MaxTake rows) so that a
        // request with a small take cannot poison later requests asking for more rows. The builds that wait for files
        // are in it: an entry stops waiting when a file it waits for is added, which queues its build, a change of the
        // dataset's tasks that removes the page.
        var cachedBytes = await _cache.GetOrCreateBytesAsync(CacheCategories.TasksList(orgSlug, dsSlug),
            async token =>
            {
                var filter = new JobIndexQueryFilter(orgSlug, dsSlug, toolId, state, Skip: 0, Take: MaxTake);
                return JsonSerializer.SerializeToUtf8Bytes(
                    await ListedAsync(await _query.QueryAsync(filter, token), dataset, token));
            }, TasksListCacheOptions, ct);
        var canonical = JsonSerializer.Deserialize<ListedTask[]>(cachedBytes) ?? [];

        // The tag of the page the caller gets: its actions are part of it
        var cachedPage = ForCaller(canonical.Length > clampedTake ? canonical[..clampedTake] : canonical, caller);
        return new TaskListPage(cachedPage, ETagOf(JsonSerializer.SerializeToUtf8Bytes(cachedPage), clampedTake));
    }

    /// <summary>A row of the task list without the caller's actions: the summary and the user the task belongs to.</summary>
    internal sealed record ListedTask(string? UserId, TaskSummaryDto Task);

    private static TaskSummaryDto[] ForCaller(IEnumerable<ListedTask> rows, TaskCaller caller) =>
        [.. rows.Select(r => r.Task with { Actions = caller.ActionsOn(r.Task.State, r.UserId) })];

    // Weak: equal tags mean the same tasks, not byte-identical bodies
    private static string ETagOf(byte[] content, int take) =>
        $"W/\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content).AsSpan(0, 12))}-{take}\"";

    /// <summary>The full status of a task, including its log tail and artifact descriptor.</summary>
    public async Task<TaskStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string id,
        CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Read, ct);
        var missing = await MissingFilesOfAsync(job, orgSlug, dsSlug, ct);
        var dataset = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        var caller = await _authorizer.CallerAsync(dataset.Dataset);
        var cancelledBy = await CancelledByAsync(job, ct);

        var (cursor, lines) = LogOf(job, cancelledBy);
        return new TaskStatusDto(
            job.JobId, job.ToolId, job.ToolVersion, job.CurrentState,
            new TaskProgressDto(job.ProgressPercent, job.PhaseMessage, null),
            job.CreatedAtUtc, job.ProcessingAtUtc, TaskJobHelpers.FinishedAt(job),
            job.ParentJobId, job.WorkflowExecutionId,
            cursor, lines, BuildArtifactDto(job, orgSlug, dsSlug),
            job.ErrorMessage ?? job.ErrorType, missing != null, missing,
            job.TargetLabel, caller.ActionsOn(job.CurrentState, job.UserId), job.WaitingForNode, cancelledBy);
    }

    // The name of the user who cancelled the task (D-038), or null
    private async Task<string?> CancelledByAsync(JobIndex job, CancellationToken ct)
    {
        if (job.CancelledBy is null)
            return null;
        var names = await NamesOfAsync([job.CancelledBy], ct);
        return names.GetValueOrDefault(job.CancelledBy, job.CancelledBy);
    }

    private async Task<IReadOnlyDictionary<string, string>> NamesOfAsync(IEnumerable<string?> userIds,
        CancellationToken ct) =>
        _userNames is null ? new Dictionary<string, string>() : await _userNames.ResolveAsync(userIds, ct);

    /// <summary>
    /// The log of a task: its tail and, for a task that a user cancelled, a last line that says who did (FR-PRC-20).
    /// The line is added when the log is read, after the job's own last write of its tail, which cannot remove it.
    /// </summary>
    private static (long Cursor, IReadOnlyList<string> Lines) LogOf(JobIndex job, string? cancelledBy)
    {
        var snapshot = LogTailSnapshot.Parse(job.LogTailJson);
        var lines = snapshot.AsStrings();
        if (cancelledBy is null || job.CurrentState != DeletedState.StateName)
            return (snapshot.Cursor, lines);

        var at = job.DeletedAtUtc ?? job.LastStateChangeUtc;
        return (snapshot.Cursor + 1, [.. lines, $"[{at:HH:mm:ss}] Cancelled by {cancelledBy}"]);
    }

    // The files the task waits for, when it is a succeeded build whose entry waits for them; the authorizer granted the
    // access, which the resolver remembers for the request
    private async Task<IReadOnlyList<string>?> MissingFilesOfAsync(JobIndex job, string orgSlug, string dsSlug,
        CancellationToken ct)
    {
        if (!BuildTaskWaits.CanWait(job))
            return null;

        var dataset = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);
        return _waits.Find([job], () => dataset.Ddb).MissingFilesOf(job.JobId);
    }

    /// <summary>The log lines of a task beyond the caller's cursor.</summary>
    public async Task<TaskLogDto> GetLogAsync(string orgSlug, string dsSlug, string id, long since,
        CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Read, ct);

        var snapshot = LogTailSnapshot.Parse(job.LogTailJson);
        var (cursor, allLines) = LogOf(job, await CancelledByAsync(job, ct));

        // Return only lines beyond the caller's cursor. The ring buffer cursor is a
        // monotonic count; the available tail starts at (cursor - lines.Count).
        var tailStart = cursor - allLines.Count;
        var skip = since > tailStart ? (int)(since - tailStart) : 0;
        skip = Math.Clamp(skip, 0, allLines.Count);
        var lines = allLines.Skip(skip).ToArray();

        return new TaskLogDto(cursor, lines, snapshot.TruncatedFromTail);
    }

    /// <summary>The file a succeeded task produced.</summary>
    public async Task<TaskArtifactFile> GetResultAsync(string orgSlug, string dsSlug, string id,
        CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Read, ct);

        if (job.CurrentState != "Succeeded" || job.ArtifactSizeBytes is null)
            throw new NotFoundException("Task has no downloadable result");

        var file = _artifacts.Resolve(id);
        if (file is null)
            throw new NotFoundException("Artifact no longer available (expired or cleaned up)");

        var etag = string.IsNullOrEmpty(job.ArtifactSha256) ? null : $"\"{job.ArtifactSha256}\"";
        return new TaskArtifactFile(file, Path.GetFileName(file),
            MimeMapping.MimeUtility.GetMimeMapping(file), etag);
    }

    private async Task<ListedTask[]> ListedAsync(JobIndex[] rows, DatasetAccess dataset, CancellationToken ct)
    {
        var waits = _waits.Find(rows, () => dataset.Ddb);
        var names = await NamesOfAsync(rows.Select(j => j.CancelledBy), ct);
        return [.. rows.Select(j => new ListedTask(j.UserId, ToSummary(j, waits.MissingFilesOf(j.JobId), names)))];
    }

    private TaskSummaryDto ToSummary(JobIndex j, IReadOnlyList<string>? missingFiles,
        IReadOnlyDictionary<string, string> names) => new(
        j.JobId, j.ToolId, j.ToolVersion, j.CurrentState, j.ProgressPercent, j.PhaseMessage,
        j.CreatedAtUtc, j.ProcessingAtUtc, TaskJobHelpers.FinishedAt(j), j.Path, j.ParentJobId,
        j.WorkflowExecutionId, j.ErrorType, j.ErrorMessage, TaskJobHelpers.ArtifactExpiresAt(j, _settings.ArtifactTtlHours),
        DdbJobDescriptor.IsBackgroundRebuildDisplay(j.MethodDisplay), missingFiles != null, missingFiles,
        j.TargetLabel, [], j.WaitingForNode,
        j.CancelledBy is null ? null : names.GetValueOrDefault(j.CancelledBy, j.CancelledBy));

    private TaskArtifactDto? BuildArtifactDto(JobIndex j, string orgSlug, string dsSlug) =>
        j.CurrentState == "Succeeded" && j.ArtifactSizeBytes is { } size
            ? new TaskArtifactDto(size, j.ArtifactSha256, $"/orgs/{orgSlug}/ds/{dsSlug}/tasks/{j.JobId}/result",
                TaskJobHelpers.ArtifactExpiresAt(j, _settings.ArtifactTtlHours))
            : null;
}
