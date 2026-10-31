using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Stac;

/// <summary>
/// The datasets a caller may list in STAC, their names, and the cache of the per-caller listings.
/// A listing costs one database query (untracked, with the organizations and their members, so the access check
/// of a member loads nothing) and no cache round trip per dataset once warm: the visibility and the name of a
/// dataset come from caches kept in process memory. The listings are keyed by the STAC generation, which
/// <see cref="IDatasetListingInvalidator"/> renews whenever a dataset changes in a way that can change them.
/// </summary>
public sealed class StacCatalog
{
    private static readonly RecordCacheOptions ListingCache = RecordCacheOptions.For(TimeSpan.FromMinutes(5));

    private readonly RegistryContext _context;
    private readonly IAuthManager _authManager;
    private readonly ICacheManager _cacheManager;
    private readonly IDdbManager _ddbManager;
    private readonly IRecordCache _records;
    private readonly CacheGenerations _generations;

    public StacCatalog(RegistryContext context, IAuthManager authManager, ICacheManager cacheManager,
        IDdbManager ddbManager, IRecordCache records, CacheGenerations generations)
    {
        _context = context;
        _authManager = authManager;
        _cacheManager = cacheManager;
        _ddbManager = ddbManager;
        _records = records;
        _generations = generations;
    }

    /// <summary>
    /// The datasets listed to the current caller (<see cref="IAuthManager.CanListDataset"/>: those they can read,
    /// without the unlisted ones of an organization they are not part of), ordered by id so that collection offsets
    /// and search tokens stay stable across requests. No native index is opened (the visibility comes from its cache).
    /// </summary>
    public Task<List<Dataset>> ListedDatasetsAsync(CancellationToken ct = default) =>
        DatasetsWhereAsync(_authManager.CanListDataset, ct);

    /// <summary>
    /// The datasets the current caller can read, in the order of <see cref="ListedDatasetsAsync"/>: the unlisted ones
    /// too, for a request that names its collections (a caller with the link of an unlisted dataset knows its id).
    /// </summary>
    public Task<List<Dataset>> ReadableDatasetsAsync(CancellationToken ct = default) =>
        DatasetsWhereAsync(ds => _authManager.RequestAccess(ds, AccessType.Read), ct);

    private async Task<List<Dataset>> DatasetsWhereAsync(Func<Dataset, Task<bool>> shown, CancellationToken ct)
    {
        var datasets = await _context.Datasets
            .AsNoTrackingWithIdentityResolution()
            .Include(ds => ds.Organization).ThenInclude(org => org.Users)
            .OrderBy(ds => ds.Id)
            .ToListAsync(ct);

        var result = new List<Dataset>(datasets.Count);
        foreach (var ds in datasets)
        {
            if (await shown(ds))
                result.Add(ds);
        }

        return result;
    }

    /// <summary>The display name of a dataset, or null when it has none.</summary>
    public Task<string?> NameAsync(Dataset ds, CancellationToken ct = default) =>
        _cacheManager.GetDatasetNameAsync(ds.Organization.Slug, ds.InternalRef, _ddbManager, ct);

    /// <summary>
    /// A listing of the current caller, cached in the current STAC generation under the key
    /// <paramref name="keyOf"/> makes of the generation and the caller's name. <paramref name="create"/> gets the
    /// shared computation's token (canceled once no caller waits for the listing).
    /// </summary>
    public async Task<T> ListingAsync<T>(Func<string, string, string> keyOf, Func<CancellationToken, Task<T>> create,
        CancellationToken ct = default)
    {
        var generation = await _generations.CurrentAsync(CacheCategories.StacGeneration, ct);
        var key = keyOf(generation, await _authManager.SafeGetCurrentUserName());
        return await _records.GetOrCreateRecordAsync(key, create, ListingCache, ct);
    }
}
