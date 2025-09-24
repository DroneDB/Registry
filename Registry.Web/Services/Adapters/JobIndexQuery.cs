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
    public async Task<List<JobIndex>> GetByOrgDsAsync(string orgSlug, string dsSlug, int skip = 0, int take = 200, CancellationToken ct = default)
        => await db.JobIndices
            .AsNoTracking()
            .Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

    public async Task<List<JobIndex>> GetByOrgDsPathAsync(string orgSlug, string dsSlug, string path, bool prefix = false, int skip = 0, int take = 200, CancellationToken ct = default)
    {
        var q = db.JobIndices.AsNoTracking().Where(x => x.OrgSlug == orgSlug && x.DsSlug == dsSlug);
        q = prefix ? q.Where(x => x.Path != null && x.Path.StartsWith(path))
            : q.Where(x => x.Path == path);
        return await q.OrderByDescending(x => x.CreatedAtUtc).Skip(skip).Take(take).ToListAsync(ct);
    }
}