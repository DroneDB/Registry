#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Services.Ports;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IDatasetCacheInvalidator"/> implementation.
/// </summary>
/// <remarks>
/// Depends only on <see cref="ICacheManager"/> and <see cref="ICacheKeyScanner"/>, both
/// registered on every host (web server and processing node), so it can run on background
/// workers that do not have the full object-management stack (<see cref="IObjectsManager"/>).
/// </remarks>
public sealed class DatasetCacheInvalidator : IDatasetCacheInvalidator
{
    private readonly ILogger<DatasetCacheInvalidator> _logger;
    private readonly ICacheManager _cacheManager;
    private readonly ICacheKeyScanner _keyScanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatasetCacheInvalidator"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cacheManager">Cache manager used for category-based invalidation.</param>
    /// <param name="keyScanner">Cache key scanner used for pattern-based (OGC) invalidation.</param>
    public DatasetCacheInvalidator(
        ILogger<DatasetCacheInvalidator> logger,
        ICacheManager cacheManager,
        ICacheKeyScanner keyScanner)
    {
        _logger = logger;
        _cacheManager = cacheManager;
        _keyScanner = keyScanner;
    }

    /// <inheritdoc />
    public async Task InvalidateAllDatasetCachesAsync(string orgSlug, string dsSlug)
    {
        _logger.LogInformation("Invalidating all caches for dataset {OrgSlug}/{DsSlug}", orgSlug, dsSlug);

        var category = ForDataset(orgSlug, dsSlug);

        // Per-file caches keyed by the dataset category.
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.TileCacheSeed, category);
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, category);
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.BuildPendingTrackerCacheSeed, category);

        // Dataset-level thumbnail cache (uses a different category).
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed,
            ForDatasetThumbnail(orgSlug, dsSlug));

        // OGC capabilities + layer enumeration caches (pattern-based, best-effort).
        try
        {
            await _keyScanner.RemoveByPatternAsync(ForOgcCapabilitiesPattern(orgSlug, dsSlug));
            await _keyScanner.RemoveByPatternAsync(ForOgcLayersPattern(orgSlug, dsSlug));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate OGC caches for {Org}/{Ds}", orgSlug, dsSlug);
        }
    }
}
