using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Datasets;

/// <summary>The datasets of an organization as places on a map.</summary>
public sealed class DatasetLocationQueries
{
    private readonly DatasetQueries _datasets;
    private readonly ICacheManager _cacheManager;
    private readonly IDdbManager _ddbManager;
    private readonly IDatasetLocationProvider _locations;
    private readonly IDatasetSummaryProvider _summaries;
    private readonly ILogger<DatasetLocationQueries> _logger;

    public DatasetLocationQueries(DatasetQueries datasets, ICacheManager cacheManager, IDdbManager ddbManager,
        IDatasetLocationProvider locations, IDatasetSummaryProvider summaries, ILogger<DatasetLocationQueries> logger)
    {
        _datasets = datasets;
        _cacheManager = cacheManager;
        _ddbManager = ddbManager;
        _locations = locations;
        _summaries = summaries;
        _logger = logger;
    }

    /// <summary>
    /// The location of each dataset of <paramref name="orgSlug"/> listed to the current user, by slug: the datasets
    /// <see cref="DatasetQueries.ListedDatasetsAsync"/> lists, so a map of the organization never shows an unlisted
    /// dataset to someone who is not part of it.
    /// </summary>
    public async Task<IReadOnlyList<DatasetLocationDto>> List(string orgSlug, CancellationToken ct = default)
    {
        var listed = await _datasets.ListedDatasetsAsync(orgSlug, ct);

        var locations = new List<DatasetLocationDto>(listed.Count);
        foreach (var (ds, _) in listed.OrderBy(r => r.Dataset.Slug, StringComparer.Ordinal))
        {
            // One native read per dataset on a cold cache: nobody waits for the map any more, stop
            ct.ThrowIfCancellationRequested();

            var location = await _locations.GetAsync(orgSlug, ds.InternalRef, ct);
            locations.Add(new DatasetLocationDto
            {
                Slug = ds.Slug,
                // The owner skips the per-dataset checks, so a dataset whose folder is gone is still listed: without a name
                Name = await PerDatasetRead.OrNullAsync(
                    () => _cacheManager.GetDatasetNameAsync(orgSlug, ds.InternalRef, _ddbManager, ct),
                    ex => _logger.LogWarning(ex, "No name for dataset {OrgSlug}/{DsSlug}", orgSlug, ds.Slug)),
                // The tagline the list's search matches, from the summary the list reads (kept per state of the index)
                Tagline = await PerDatasetRead.OrNullAsync(
                    () => Task.FromResult(DatasetListQuery.TaglineOf(_summaries.Get(orgSlug, ds.InternalRef).Properties)),
                    ex => _logger.LogWarning(ex, "No tagline for dataset {OrgSlug}/{DsSlug}", orgSlug, ds.Slug)),
                Bbox = location?.Bbox,
                Point = location?.Point
            });
        }

        return locations;
    }
}
