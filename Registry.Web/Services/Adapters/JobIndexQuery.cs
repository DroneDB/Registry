#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

public class JobIndexQuery(RegistryContext db) : IJobIndexQuery
{
    public async Task<JobIndex[]> GetByOrgDsAsync(string orgSlug, string dsSlug, int skip = 0, int take = 200, CancellationToken ct = default)
        => await db.JobIndices
            .AsNoTracking()
            .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(skip).Take(take)
            .ToArrayAsync(ct);

    public async Task<JobIndex[]> GetByOrgDsHashAsync(string orgSlug, string dsSlug, string hash, int skip = 0, int take = 200, CancellationToken ct = default)
    {
        var q =
            db.JobIndices.AsNoTracking().Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug && x.Hash == hash);
        return await q.OrderByDescending(x => x.CreatedAtUtc).Skip(skip).Take(take).ToArrayAsync(cancellationToken: ct);
    }

    public async Task<JobIndex[]> GetByStateAsync(string state, int skip = 0, int take = 1000, CancellationToken ct = default)
        => await db.JobIndices
            .AsNoTracking()
            .Where(x => x.CurrentState == state)
            .OrderBy(x => x.CreatedAtUtc)
            .Skip(skip).Take(take)
            .ToArrayAsync(ct);

    // Non-terminal states a task can be in while still running.
    private static readonly string[] ActiveStates = ["Created", "Enqueued", "Scheduled", "Processing"];

    public async Task<JobIndex[]> QueryAsync(JobIndexQueryFilter filter, CancellationToken ct = default)
    {
        var q = db.JobIndices.AsNoTracking()
            .Where(x => x.OrgSlug == filter.OrgSlug && x.DsSlug == filter.DsSlug);

        if (!string.IsNullOrEmpty(filter.ToolId))
            q = q.Where(x => x.ToolId == filter.ToolId);
        if (!string.IsNullOrEmpty(filter.State))
            q = q.Where(x => x.CurrentState == filter.State);
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

    public async Task<JobIndex?> FindDedupCandidateAsync(
        string orgSlug, string dsSlug, string toolId, string requestHash,
        int lookbackHours, CancellationToken ct = default)
    {
        var cutoff = System.DateTime.UtcNow.AddHours(-lookbackHours);

        return await db.JobIndices.AsNoTracking()
            .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug
                        && x.ToolId == toolId && x.RequestHash == requestHash)
            .Where(x => ActiveStates.Contains(x.CurrentState)
                        || (x.CurrentState == "Succeeded" && x.CreatedAtUtc >= cutoff))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<long> CountActiveAsync(string? orgSlug = null, string? userId = null, CancellationToken ct = default)
    {
        var q = db.JobIndices.AsNoTracking()
            .Where(x => ActiveStates.Contains(x.CurrentState));

        if (!string.IsNullOrEmpty(orgSlug))
            q = q.Where(x => x.OrgSlug == orgSlug);
        if (!string.IsNullOrEmpty(userId))
            q = q.Where(x => x.UserId == userId);

        return await q.LongCountAsync(ct);
    }
}