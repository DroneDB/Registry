using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Query operations on the JobIndex table for Hangfire job tracking.
/// </summary>
public class JobIndexQuery(RegistryContext db) : IJobIndexQuery
{
    public async Task<JobIndex[]> GetByOrgDsAsync(string orgSlug, string dsSlug, int skip = 0, int take = 200, CancellationToken ct = default)
        => await db.JobIndices
            .AsNoTracking()
            .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(skip).Take(take)
            .ToArrayAsync(ct);

    // Bounds the IN list of path filters (SQLite and MySQL parameter limits)
    private const int PathChunkSize = 500;

    public async Task<HashSet<string>> GetActivePathsAsync(string orgSlug, string dsSlug, string toolId,
        IReadOnlyCollection<string> paths, CancellationToken ct = default) =>
        (await GetActiveJobsAsync(orgSlug, dsSlug, toolId, paths, ct)).Select(j => j.Path).ToHashSet();

    public async Task<IReadOnlyList<ActiveJob>> GetActiveJobsAsync(string orgSlug, string dsSlug, string toolId,
        IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var result = new List<ActiveJob>();

        foreach (var chunk in paths.Distinct().Chunk(PathChunkSize))
        {
            var active = await db.JobIndices
                .AsNoTracking()
                .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug && x.ToolId == toolId &&
                            chunk.Contains(x.Path!) && ActiveStates.Contains(x.CurrentState))
                .Select(x => new { Path = x.Path!, x.CurrentState, x.MethodDisplay })
                .ToArrayAsync(ct);

            result.AddRange(active.Select(x => new ActiveJob(x.Path, x.CurrentState, x.MethodDisplay)));
        }

        return result;
    }

    public async Task<Dictionary<string, string>> GetLatestStatesByPathAsync(string orgSlug, string dsSlug,
        string toolId, IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();

        foreach (var chunk in paths.Distinct().Chunk(PathChunkSize))
        {
            // Three narrow columns of the listed paths' jobs; the latest per path is picked here
            var jobs = await db.JobIndices
                .AsNoTracking()
                .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug && x.ToolId == toolId &&
                            chunk.Contains(x.Path!))
                .Select(x => new { Path = x.Path!, x.CurrentState, x.CreatedAtUtc })
                .ToArrayAsync(ct);

            foreach (var latest in jobs.GroupBy(x => x.Path)
                         .Select(g => g.MaxBy(x => x.CreatedAtUtc)!))
                result[latest.Path] = latest.CurrentState;
        }

        return result;
    }

    public async Task<JobIndex?> GetByJobIdAsync(string orgSlug, string dsSlug, string jobId, CancellationToken ct = default)
        => await db.JobIndices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug && x.JobId == jobId, ct);

    public async Task<JobIndex[]> GetByStateAsync(string state, int skip = 0, int take = 1000, CancellationToken ct = default)
        => await db.JobIndices
            .AsNoTracking()
            .Where(x => x.CurrentState == state)
            .OrderBy(x => x.CreatedAtUtc)
            .Skip(skip).Take(take)
            .ToArrayAsync(ct);

    // Non-terminal states a task can be in while still running (canonical catalog).
    private static readonly string[] ActiveStates = TaskStateCatalog.Active;

    /// <summary>
    /// The state filter of the task lists (D-024, FR-PRC-37): a task that waits in the queue of a processing node
    /// (<c>WaitingForNode</c>) is listed as <c>Enqueued</c>, not as <c>Processing</c>, although its job runs.
    /// </summary>
    private static IQueryable<JobIndex> WhereState(IQueryable<JobIndex> q, string state) => state switch
    {
        EnqueuedState => q.Where(x => x.CurrentState == EnqueuedState
                                      || (x.CurrentState == ProcessingState && x.WaitingForNode)),
        ProcessingState => q.Where(x => x.CurrentState == ProcessingState && !x.WaitingForNode),
        _ => q.Where(x => x.CurrentState == state)
    };

    private const string EnqueuedState = "Enqueued";
    private const string ProcessingState = "Processing";

    public async Task<JobIndex[]> QueryAsync(JobIndexQueryFilter filter, CancellationToken ct = default)
    {
        var q = db.JobIndices.AsNoTracking()
            .Where(x => x.OrgSlug == filter.OrgSlug && x.DsSlug == filter.DsSlug);

        if (!string.IsNullOrEmpty(filter.ToolId))
            q = q.Where(x => x.ToolId == filter.ToolId);
        if (!string.IsNullOrEmpty(filter.State))
            q = WhereState(q, filter.State);
        if (!string.IsNullOrEmpty(filter.Path))
            q = q.Where(x => x.Path == filter.Path);
        if (!string.IsNullOrEmpty(filter.UserId))
            q = q.Where(x => x.UserId == filter.UserId);
        if (!string.IsNullOrEmpty(filter.WorkflowExecutionId))
            q = q.Where(x => x.WorkflowExecutionId == filter.WorkflowExecutionId);
        if (!string.IsNullOrEmpty(filter.ParentJobId))
            q = q.Where(x => x.ParentJobId == filter.ParentJobId);

        return await q
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(filter.Skip).Take(filter.Take)
            .ToArrayAsync(ct);
    }

    public async Task<JobIndex[]> QueryGlobalAsync(JobIndexGlobalQueryFilter filter, CancellationToken ct = default)
    {
        var q = ApplyGlobalFilters(filter.ToolId, filter.State, filter.UserId);

        return await ApplyGlobalOrder(q, filter.SortField, filter.SortOrder)
            .Skip(filter.Skip).Take(filter.Take)
            .ToArrayAsync(ct);
    }

    // The whitelisted order of the admin list (FR-PRC-28): the chosen column in the chosen direction, then the
    // newest first and the job id, so rows that tie never swap between pages. Only these columns can be sorted by.
    private static IOrderedQueryable<JobIndex> ApplyGlobalOrder(IQueryable<JobIndex> q,
        JobIndexSortField field, JobIndexSortOrder order)
    {
        var desc = order == JobIndexSortOrder.Descending;
        switch (field)
        {
            case JobIndexSortField.CreatedAt:
                return (desc ? q.OrderByDescending(x => x.CreatedAtUtc) : q.OrderBy(x => x.CreatedAtUtc))
                    .ThenBy(x => x.JobId);
            case JobIndexSortField.State:
                return (desc ? q.OrderByDescending(x => x.CurrentState) : q.OrderBy(x => x.CurrentState))
                    .ThenByDescending(x => x.CreatedAtUtc).ThenBy(x => x.JobId);
            case JobIndexSortField.ToolId:
                return (desc ? q.OrderByDescending(x => x.ToolId) : q.OrderBy(x => x.ToolId))
                    .ThenByDescending(x => x.CreatedAtUtc).ThenBy(x => x.JobId);
            default:
                throw new System.ArgumentOutOfRangeException(nameof(field), field, "Not a sortable column of the task list");
        }
    }

    public async Task<long> CountGlobalAsync(string? toolId, string? state, string? userId, CancellationToken ct = default)
        => await ApplyGlobalFilters(toolId, state, userId).LongCountAsync(ct);

    private IQueryable<JobIndex> ApplyGlobalFilters(string? toolId, string? state, string? userId)
    {
        var q = db.JobIndices.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(toolId))
            q = q.Where(x => x.ToolId == toolId);
        if (!string.IsNullOrEmpty(state))
            q = WhereState(q, state);
        if (!string.IsNullOrEmpty(userId))
            q = q.Where(x => x.UserId == userId);

        return q;
    }

    public async Task<JobIndex?> FindDedupCandidateAsync(
        string orgSlug, string dsSlug, string toolId, string requestHash,
        int lookbackHours, TaskOwnerScope? ownedBy = null, CancellationToken ct = default)
    {
        var cutoff = System.DateTime.UtcNow.AddHours(-lookbackHours);

        return await SameRequest(orgSlug, dsSlug, toolId, requestHash, ownedBy)
            .Where(x => ActiveStates.Contains(x.CurrentState)
                        || (x.CurrentState == "Succeeded" && x.CreatedAtUtc >= cutoff))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<JobIndex?> FindActiveDedupCandidateAsync(
        string orgSlug, string dsSlug, string toolId, string requestHash, TaskOwnerScope? ownedBy = null,
        CancellationToken ct = default) =>
        await SameRequest(orgSlug, dsSlug, toolId, requestHash, ownedBy)
            .Where(x => ActiveStates.Contains(x.CurrentState))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

    // The task records of the dataset with the same tool and request hash (of one user, or of no user, with ownedBy)
    private IQueryable<JobIndex> SameRequest(string orgSlug, string dsSlug, string toolId, string requestHash,
        TaskOwnerScope? ownedBy)
    {
        var q = db.JobIndices.AsNoTracking()
            .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug
                        && x.ToolId == toolId && x.RequestHash == requestHash);
        if (ownedBy is null)
            return q;

        var userId = ownedBy.UserId;
        return userId is null ? q.Where(x => x.UserId == null) : q.Where(x => x.UserId == userId);
    }

    public async Task<IReadOnlyList<ActiveTaskOutputs>> GetActiveOutputsAsync(string orgSlug, string dsSlug,
        CancellationToken ct = default)
    {
        var rows = await db.JobIndices.AsNoTracking()
            .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug && x.OutputPathsJson != null
                        && ActiveStates.Contains(x.CurrentState))
            .Select(x => new { x.JobId, x.ToolId, x.OutputPathsJson })
            .ToListAsync(ct);

        var result = new List<ActiveTaskOutputs>(rows.Count);
        foreach (var row in rows)
        {
            var paths = ReadPaths(row.OutputPathsJson);
            if (paths.Length > 0)
                result.Add(new ActiveTaskOutputs(row.JobId, row.ToolId, paths));
        }
        return result;
    }

    // The JSON array of a row; a value that is not one (never written so) declares nothing
    private static string[] ReadPaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<long> CountActiveUserTasksAsync(string? orgSlug = null, string? userId = null,
        string? toolId = null, CancellationToken ct = default)
    {
        var q = db.JobIndices.AsNoTracking().Where(UserTasks.CountTowardLimits);

        if (!string.IsNullOrEmpty(orgSlug))
            q = q.Where(x => x.OrgSlug == orgSlug);
        if (!string.IsNullOrEmpty(userId))
            q = q.Where(x => x.UserId == userId);
        if (!string.IsNullOrEmpty(toolId))
            q = q.Where(x => x.ToolId == toolId);

        return await q.LongCountAsync(ct);
    }
}