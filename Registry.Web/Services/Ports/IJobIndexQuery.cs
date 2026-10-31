using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// JobIndex query interface for Hangfire job tracking (list, filter, count).
/// </summary>
public interface IJobIndexQuery
{
    Task<JobIndex[]> GetByOrgDsAsync(string orgSlug, string dsSlug, int skip = 0, int take = 200, CancellationToken ct = default);

    /// <summary>Single task lookup scoped to its dataset, for per-task status/ownership checks.</summary>
    Task<JobIndex?> GetByJobIdAsync(string orgSlug, string dsSlug, string jobId, CancellationToken ct = default);

    /// <summary>
    /// The given paths that have a not-yet-finished job of <paramref name="toolId"/>.
    /// Reads only the active rows of those paths.
    /// </summary>
    Task<HashSet<string>> GetActivePathsAsync(string orgSlug, string dsSlug, string toolId,
        IReadOnlyCollection<string> paths, CancellationToken ct = default);

    /// <summary>
    /// The not-yet-finished jobs of <paramref name="toolId"/> of the given paths (a path may have several), with
    /// what tells a waiting background rebuild from a running build. Reads only the active rows of those paths.
    /// </summary>
    Task<IReadOnlyList<ActiveJob>> GetActiveJobsAsync(string orgSlug, string dsSlug, string toolId,
        IReadOnlyCollection<string> paths, CancellationToken ct = default);

    /// <summary>
    /// State of the most recent <paramref name="toolId"/> job of each given path that has one.
    /// Reads only the jobs of those paths, never the whole dataset history.
    /// </summary>
    Task<Dictionary<string, string>> GetLatestStatesByPathAsync(string orgSlug, string dsSlug, string toolId,
        IReadOnlyCollection<string> paths, CancellationToken ct = default);
    Task<JobIndex[]> GetByStateAsync(string state, int skip = 0, int take = 1000, CancellationToken ct = default);

    /// <summary>Flexible TaskHistory query filtered by tool/state/path/user/workflow.</summary>
    Task<JobIndex[]> QueryAsync(JobIndexQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Global (cross org/dataset) task query for the admin dashboard.
    /// Filtered by tool/state/user with server-side paging, ordered by creation desc.
    /// </summary>
    Task<JobIndex[]> QueryGlobalAsync(JobIndexGlobalQueryFilter filter, CancellationToken ct = default);

    /// <summary>Counts all tasks matching the global filter, for paging totals.</summary>
    Task<long> CountGlobalAsync(string? toolId, string? state, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Finds an existing active or recently-succeeded task with the same dedup hash,
    /// for submit deduplication.
    /// </summary>
    Task<JobIndex?> FindDedupCandidateAsync(
        string orgSlug, string dsSlug, string toolId, string requestHash,
        int lookbackHours, CancellationToken ct = default);

    /// <summary>
    /// Counts the active tasks of users (<see cref="HeavyTasks.UserTasks.CountTowardLimits"/>: no builds, no
    /// jobs of the automatic build service or of no user), optionally scoped by org, user and/or tool.
    /// </summary>
    Task<long> CountActiveUserTasksAsync(string? orgSlug = null, string? userId = null,
        string? toolId = null, CancellationToken ct = default);
}

/// <summary>A job of an entry that has not finished: its Hangfire state and the method text of its row.</summary>
public readonly record struct ActiveJob(string Path, string State, string? MethodDisplay);

public sealed record JobIndexQueryFilter(
    string OrgSlug, string DsSlug,
    string? ToolId = null, string? State = null, string? Path = null,
    string? UserId = null, string? WorkflowExecutionId = null, string? ParentJobId = null,
    int Skip = 0, int Take = 50);

/// <summary>
/// Global task filter for the admin dashboard. Unlike <see cref="JobIndexQueryFilter"/>
/// it is not scoped to a single org/dataset; all filters are optional.
/// </summary>
public sealed record JobIndexGlobalQueryFilter(
    string? ToolId = null,
    string? State = null,
    string? UserId = null,
    int Skip = 0,
    int Take = 50);