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
}