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
}