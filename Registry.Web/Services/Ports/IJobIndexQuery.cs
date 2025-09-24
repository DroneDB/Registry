#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Data.Models;

namespace Registry.Web.Services.Ports;

public interface IJobIndexQuery
{
    Task<List<JobIndex>> GetByOrgDsAsync(string orgSlug, string dsSlug, int skip = 0, int take = 200, CancellationToken ct = default);
    Task<List<JobIndex>> GetByOrgDsPathAsync(string orgSlug, string dsSlug, string path, bool prefix = false, int skip = 0, int take = 200, CancellationToken ct = default);
}