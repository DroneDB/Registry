#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Objects;

/// <summary>
/// The caches an entry command invalidates at once (uploads coalesce theirs, <see cref="IDatasetChangeCoalescer"/>):
/// the dataset thumbnail when a thumbnail candidate changed, and the dataset's OGC documents.
/// </summary>
public sealed class EntryChangeNotifier
{
    private readonly ICacheManager _cache;
    private readonly IOgcLayerCatalog _ogcCatalog;
    private readonly string[] _thumbnailCandidates;
    private readonly ILogger<EntryChangeNotifier> _logger;

    public EntryChangeNotifier(ICacheManager cache, IOgcLayerCatalog ogcCatalog, IOptions<ThumbnailOptions> thumbnails,
        ILogger<EntryChangeNotifier> logger)
    {
        _cache = cache;
        _ogcCatalog = ogcCatalog;
        _thumbnailCandidates = thumbnails.Value.Candidates;
        _logger = logger;
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
