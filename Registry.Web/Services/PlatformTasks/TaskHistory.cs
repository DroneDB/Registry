using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ProcessingPlatformSettings _settings;

    public TaskHistory(IDatasetAccessResolver access, IJobIndexQuery query, IRecordCache cache,
        TaskAuthorizer authorizer, TaskArtifactStore artifacts, IOptions<ProcessingPlatformSettings> platformSettings)
    {
        _access = access;
        _query = query;
        _cache = cache;
        _authorizer = authorizer;
        _artifacts = artifacts;
        _settings = platformSettings.Value;
    }

    /// <summary>The page of the dataset's task history, newest first, with the entity tag of the page.</summary>
    public async Task<TaskListPage> ListPageAsync(string orgSlug, string dsSlug, string? toolId, string? state,
        int skip, int take, CancellationToken ct = default)
    {
        await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Read, ct: ct);

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
            TaskSummaryDto[] page = [.. rows.Select(ToSummary)];
            return new TaskListPage(page, ETagOf(JsonSerializer.SerializeToUtf8Bytes(page), clampedTake));
        }

        // The cached payload is always the canonical page (first MaxTake rows) so that a
        // request with a small take cannot poison later requests asking for more rows.
        var cachedBytes = await _cache.GetOrCreateBytesAsync(CacheCategories.TasksList(orgSlug, dsSlug),
            async token =>
            {
                var filter = new JobIndexQueryFilter(orgSlug, dsSlug, toolId, state, Skip: 0, Take: MaxTake);
                TaskSummaryDto[] rows = [.. (await _query.QueryAsync(filter, token)).Select(ToSummary)];
                return JsonSerializer.SerializeToUtf8Bytes(rows);
            }, TasksListCacheOptions, ct);
        var canonical = JsonSerializer.Deserialize<TaskSummaryDto[]>(cachedBytes) ?? [];

        // The tag of a page of the canonical list: the canonical bytes and the page size
        return new TaskListPage(canonical.Length > clampedTake ? canonical[..clampedTake] : canonical,
            ETagOf(cachedBytes, clampedTake));
    }

    // Weak: equal tags mean the same tasks, not byte-identical bodies
    private static string ETagOf(byte[] content, int take) =>
        $"W/\"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content).AsSpan(0, 12))}-{take}\"";

    /// <summary>The full status of a task, including its log tail and artifact descriptor.</summary>
    public async Task<TaskStatusDto> GetStatusAsync(string orgSlug, string dsSlug, string id,
        CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Read, ct);

        var snapshot = LogTailSnapshot.Parse(job.LogTailJson);
        return new TaskStatusDto(
            job.JobId, job.ToolId, job.ToolVersion, job.CurrentState,
            new TaskProgressDto(job.ProgressPercent, job.PhaseMessage, null),
            job.CreatedAtUtc, job.ProcessingAtUtc, TaskJobHelpers.FinishedAt(job),
            job.ParentJobId, job.WorkflowExecutionId,
            snapshot.Cursor, snapshot.AsStrings(), BuildArtifactDto(job, orgSlug, dsSlug),
            job.ErrorType);
    }

    /// <summary>The log lines of a task beyond the caller's cursor.</summary>
    public async Task<TaskLogDto> GetLogAsync(string orgSlug, string dsSlug, string id, long since,
        CancellationToken ct = default)
    {
        var job = await _authorizer.LoadAsync(orgSlug, dsSlug, id, AccessType.Read, ct);

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

    private TaskSummaryDto ToSummary(JobIndex j) => new(
        j.JobId, j.ToolId, j.ToolVersion, j.CurrentState, j.ProgressPercent, j.PhaseMessage,
        j.CreatedAtUtc, j.ProcessingAtUtc, TaskJobHelpers.FinishedAt(j), j.Path, j.ParentJobId,
        j.WorkflowExecutionId, j.ErrorType, TaskJobHelpers.ArtifactExpiresAt(j, _settings.ArtifactTtlHours),
        DdbJobDescriptor.IsBackgroundRebuildDisplay(j.MethodDisplay));

    private TaskArtifactDto? BuildArtifactDto(JobIndex j, string orgSlug, string dsSlug) =>
        j.CurrentState == "Succeeded" && j.ArtifactSizeBytes is { } size
            ? new TaskArtifactDto(size, j.ArtifactSha256, $"/orgs/{orgSlug}/ds/{dsSlug}/tasks/{j.JobId}/result",
                TaskJobHelpers.ArtifactExpiresAt(j, _settings.ArtifactTtlHours))
            : null;
}
