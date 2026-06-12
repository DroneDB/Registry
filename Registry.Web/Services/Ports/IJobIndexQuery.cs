#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports;

public interface IJobIndexQuery
{
    Task<JobIndex[]> GetByOrgDsAsync(string orgSlug, string dsSlug, int skip = 0, int take = 200, CancellationToken ct = default);
    Task<JobIndex[]> GetByOrgDsHashAsync(string orgSlug, string dsSlug, string hash, int skip = 0, int take = 200, CancellationToken ct = default);
    Task<JobIndex[]> GetByStateAsync(string state, int skip = 0, int take = 1000, CancellationToken ct = default);

    /// <summary>Flexible TaskHistory query filtered by tool/state/path/user/workflow.</summary>
    Task<JobIndex[]> QueryAsync(JobIndexQueryFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Global (cross org/dataset) task query for the admin dashboard (spec §B.1.1).
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

    /// <summary>Counts active (non-terminal) tasks, optionally scoped by org, user and/or tool.</summary>
    Task<long> CountActiveAsync(string? orgSlug = null, string? userId = null,
        string? toolId = null, CancellationToken ct = default);
}

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