#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Builds;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Objects;

/// <summary>
/// What an entry command changes beyond its entries, at once (uploads coalesce their caches,
/// <see cref="IDatasetChangeCoalescer"/>): the dataset thumbnail when a thumbnail candidate changed, the dataset's OGC
/// documents, and, for entries that arrive, the pending builds that waited for them.
/// </summary>
public sealed class EntryChangeNotifier
{
    private readonly ICacheManager _cache;
    private readonly IOgcLayerCatalog _ogcCatalog;
    private readonly BuildScheduler _builds;
    private readonly string[] _thumbnailCandidates;
    private readonly ILogger<EntryChangeNotifier> _logger;

    public EntryChangeNotifier(ICacheManager cache, IOgcLayerCatalog ogcCatalog, BuildScheduler builds,
        IOptions<ThumbnailOptions> thumbnails, ILogger<EntryChangeNotifier> logger)
    {
        _cache = cache;
        _ogcCatalog = ogcCatalog;
        _builds = builds;
        _thumbnailCandidates = thumbnails.Value.Candidates;
        _logger = logger;
    }

    /// <summary>
    /// Entries at <paramref name="paths"/> arrived in the dataset (moved, copied or transferred into it): the caches of
    /// <see cref="EntriesChangedAsync"/>, then the retry of the pending builds that waited for a file of that name
    /// (<see cref="BuildScheduler.RetryPendingAsync"/>, which libddb made due; it never throws).
    /// </summary>
    public async Task EntriesArrivedAsync(DatasetAccess access, string orgSlug, string dsSlug, IEnumerable<string> paths)
    {
        await EntriesChangedAsync(orgSlug, dsSlug, paths);
        await _builds.RetryPendingAsync(BuildTarget.Of(access, orgSlug, dsSlug));
    }

    /// <summary>Entries at <paramref name="paths"/> were added, changed or removed.</summary>
    public async Task EntriesChangedAsync(string orgSlug, string dsSlug, IEnumerable<string> paths)
    {
        if (paths.Any(p => DatasetThumbnails.IsCandidateFile(p, _thumbnailCandidates)))
            await InvalidateDatasetThumbnailAsync(orgSlug, dsSlug);

        await _ogcCatalog.InvalidateAsync(orgSlug, dsSlug);
    }

    private async Task InvalidateDatasetThumbnailAsync(string orgSlug, string dsSlug)
    {
        _logger.LogDebug("Invalidating dataset thumbnail cache for {OrgSlug}/{DsSlug}", orgSlug, dsSlug);
        await _cache.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, ForDatasetThumbnail(orgSlug, dsSlug));
    }
}
