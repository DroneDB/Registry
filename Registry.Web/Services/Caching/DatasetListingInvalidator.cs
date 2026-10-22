#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Caching;

/// <summary>
/// What may change who can list a dataset, or how it is listed (RQ-12): its visibility, its name, its slug, its
/// existence. The one place that drops the caches those answers live in, so no path that changes one of them can
/// forget a cache: the dataset's cached visibility, and every STAC listing (the landing page and the collections
/// listing are cached per caller, so they are keyed by one STAC generation that is renewed here).
/// </summary>
public interface IDatasetListingInvalidator
{
    /// <summary>The dataset's visibility may have changed: its cached visibility and every listing go.</summary>
    Task VisibilityChangedAsync(string orgSlug, Guid internalRef, CancellationToken ct = default);

    /// <summary>The dataset's name, slugs or existence changed: every listing (and its cached name) goes.</summary>
    Task ListingChangedAsync(Guid internalRef, CancellationToken ct = default);
}

/// <summary>Default <see cref="IDatasetListingInvalidator"/>.</summary>
public sealed class DatasetListingInvalidator(ICacheManager cacheManager, IDdbManager ddbManager,
    ICacheGenerations generations, IRecordCache records) : IDatasetListingInvalidator
{
    public async Task VisibilityChangedAsync(string orgSlug, Guid internalRef, CancellationToken ct = default)
    {
        // The visibility first: a listing computed after the new generation reads the new visibility
        await cacheManager.RemoveDatasetVisibilityAsync(orgSlug, internalRef, ddbManager);
        await ListingChangedAsync(internalRef, ct);
    }

    public async Task ListingChangedAsync(Guid internalRef, CancellationToken ct = default)
    {
        await records.RemoveAsync(CacheCategories.StacDatasetName(internalRef.ToString()), ct);
        await records.RemoveAsync(CacheCategories.StacCatalogLink(internalRef.ToString()), ct);
        await generations.RenewAsync(CacheCategories.StacGeneration, ct);
    }
}
