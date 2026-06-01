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
    /// Finds an existing active or recently-succeeded task with the same dedup hash,
    /// for submit deduplication.
    /// </summary>
    Task<JobIndex?> FindDedupCandidateAsync(
        string orgSlug, string dsSlug, string toolId, string requestHash,
        int lookbackHours, CancellationToken ct = default);

    /// <summary>Counts active (non-terminal) tasks, optionally scoped by org and/or user.</summary>
    Task<long> CountActiveAsync(string? orgSlug = null, string? userId = null, CancellationToken ct = default);
}

public sealed record JobIndexQueryFilter(
    string OrgSlug, string DsSlug,
    string? ToolId = null, string? State = null, string? Path = null,
    string? UserId = null, string? WorkflowExecutionId = null, string? ParentJobId = null,
    int Skip = 0, int Take = 50);