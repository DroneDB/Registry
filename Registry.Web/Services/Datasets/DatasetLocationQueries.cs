using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Datasets;

/// <summary>The datasets of an organization as places on a map.</summary>
public sealed class DatasetLocationQueries
{
    private readonly DatasetQueries _datasets;
    private readonly IAuthManager _authManager;
    private readonly ICacheManager _cacheManager;
    private readonly IDdbManager _ddbManager;
    private readonly IDatasetLocationProvider _locations;
    private readonly ILogger<DatasetLocationQueries> _logger;

    public DatasetLocationQueries(DatasetQueries datasets, IAuthManager authManager, ICacheManager cacheManager,
        IDdbManager ddbManager, IDatasetLocationProvider locations, ILogger<DatasetLocationQueries> logger)
    {
        _datasets = datasets;
        _authManager = authManager;
        _cacheManager = cacheManager;
        _ddbManager = ddbManager;
        _locations = locations;
        _logger = logger;
    }

    /// <summary>
    /// The location of each dataset of <paramref name="orgSlug"/> the current user can read, by slug: the datasets
    /// <see cref="DatasetQueries.ReadableDatasetsAsync"/> lists. A dataset that is unlisted is left out unless the user
    /// owns, administers or belongs to the organization: a link to it is how the others find it, so a map of the
    /// organization must not publish it.
    /// </summary>
    public async Task<IReadOnlyList<DatasetLocationDto>> List(string orgSlug, CancellationToken ct = default)
    {
        var (organization, isOwnerOrAdmin, readable) = await _datasets.ReadableDatasetsAsync(orgSlug, ct);

        var userId = await _authManager.GetCurrentUserId();
        var knowsTheOrganization = isOwnerOrAdmin ||
                                   (userId != null && organization.Users?.Any(u => u.UserId == userId) == true);

        var locations = new List<DatasetLocationDto>(readable.Count);
        foreach (var (ds, _) in readable.OrderBy(r => r.Dataset.Slug, StringComparer.Ordinal))
        {
            // One native read per dataset on a cold cache: nobody waits for the map any more, stop
            ct.ThrowIfCancellationRequested();

            if (!knowsTheOrganization &&
                await _cacheManager.GetDatasetVisibilityAsync(orgSlug, ds.InternalRef, _ddbManager) == Visibility.Unlisted)
                continue;

            locations.Add(new DatasetLocationDto
            {
                Slug = ds.Slug,
                // The owner skips the per-dataset checks, so a dataset whose folder is gone is still listed: without a name
                Name = await PerDatasetRead.OrNullAsync(
                    () => _cacheManager.GetDatasetNameAsync(orgSlug, ds.InternalRef, _ddbManager, ct),
                    ex => _logger.LogWarning(ex, "No name for dataset {OrgSlug}/{DsSlug}", orgSlug, ds.Slug)),
                Bbox = (await _locations.GetAsync(orgSlug, ds.InternalRef, ct))?.Bbox
            });
        }

        return locations;
    }
}
