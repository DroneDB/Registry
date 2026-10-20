#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Hangfire;
using Registry.Web.Utilities;
using Registry.Ports;
using Registry.Web.Services.Ports;
using static Registry.Web.Services.CacheCategories;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Default <see cref="IDatasetCacheInvalidator"/> implementation.
/// </summary>
/// <remarks>
/// Observation caches use <see cref="ICacheManager"/>; OGC documents a new <see cref="IOgcCacheKeys"/> generation.
/// Available legacy retry controls are preserved in configured Hangfire storage before
/// their first tracker eviction. These services are available on processing nodes too.
/// </remarks>
public sealed class DatasetCacheInvalidator : IDatasetCacheInvalidator
{
    private readonly ILogger<DatasetCacheInvalidator> _logger;
    private readonly ICacheManager _cacheManager;
    private readonly IOgcCacheKeys _ogcKeys;
    private readonly JobStorage? _storage;
    private readonly IConfiguration? _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatasetCacheInvalidator"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cacheManager">Cache manager used for category-based invalidation.</param>
    /// <param name="ogcKeys">Generation of the OGC capabilities and layer caches.</param>
    /// <param name="storage">Configured Hangfire storage for preserving legacy retry controls.</param>
    /// <param name="configuration">Configured Hangfire MySQL connection source.</param>
    public DatasetCacheInvalidator(
        ILogger<DatasetCacheInvalidator> logger,
        ICacheManager cacheManager,
        IOgcCacheKeys ogcKeys,
        JobStorage? storage = null,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _cacheManager = cacheManager;
        _ogcKeys = ogcKeys;
        _storage = storage;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task InvalidateAllDatasetCachesAsync(string orgSlug, string dsSlug)
    {
        _logger.LogInformation("Invalidating all caches for dataset {OrgSlug}/{DsSlug}", orgSlug, dsSlug);

        var category = ForDataset(orgSlug, dsSlug);

        // Per-file caches keyed by the dataset category.
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.TileCacheSeed, category);
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed, category);
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.OgcRenderCacheSeed, category);
        // Retry controls are authoritative in Hangfire; evicting these observations
        // cannot erase a live coordinator or replenish its bounded budget.
        if (await PreserveLegacyRetryControls(orgSlug, dsSlug, category))
            await _cacheManager.RemoveByCategoryAsync(MagicStrings.BuildPendingTrackerCacheSeed, category);

        // Dataset-level thumbnail cache (uses a different category).
        await _cacheManager.RemoveByCategoryAsync(MagicStrings.ThumbnailCacheSeed,
            ForDatasetThumbnail(orgSlug, dsSlug));

        // OGC capabilities + layer enumeration caches (one new generation, best-effort).
        try
        {
            await _ogcKeys.InvalidateAsync(orgSlug, dsSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate OGC caches for {Org}/{Ds}", orgSlug, dsSlug);
        }
    }

    private async Task<bool> PreserveLegacyRetryControls(string orgSlug, string dsSlug, string category)
    {
        if (!_cacheManager.IsRegistered(MagicStrings.BuildPendingTrackerCacheSeed)) return true;
        try
        {
            var observation = await _cacheManager.GetAsync(MagicStrings.BuildPendingTrackerCacheSeed, category);
            if (!PendingBuildRetryControlStore.HasLegacyControls(observation)) return true;
            var storage = _storage ?? JobStorage.Current;
            using var coordination = DatasetBuildCoordinator.Acquire(orgSlug, dsSlug, storage,
                mysqlConnectionString: _configuration?.GetConnectionString(MagicStrings.HangfireConnectionName));
            using var connection = storage.GetConnection();
            var freshObservation = await _cacheManager.GetAsync(MagicStrings.BuildPendingTrackerCacheSeed, category);
            PendingBuildRetryControlStore.Load(connection, DatasetBuildCoordinator.RetryStateKey(orgSlug, dsSlug),
                PendingBuildRetryControlStore.HasLegacyControls(freshObservation) ? freshObservation : observation, _logger);
            return true;
        }
        catch (Exception ex)
        {
            // Keep the only available legacy observation if shared storage cannot
            // preserve it yet. Other dataset cache invalidation still proceeds.
            _logger.LogWarning(ex, "Cannot preserve legacy pending controls for {Org}/{Ds}; keeping tracker observation",
                orgSlug, dsSlug);
            return false;
        }
    }
}
