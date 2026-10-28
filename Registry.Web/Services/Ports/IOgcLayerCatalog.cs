#nullable disable
using System.Collections.Generic;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Enumerates layers exposed via OGC for a dataset (raster + vector + optionally multi-layer GPKG inner layers).
/// Cached for <c>OgcLayerCatalogCacheMinutes</c> in Redis.
/// </summary>
public interface IOgcLayerCatalog
{
    /// <summary>
    /// Get all OGC layers in the given dataset, optionally restricted to a sub-folder.
    /// Local layers (<see cref="OgcLayerDto.IsLocal"/>) are left out unless
    /// <paramref name="includeLocal"/> is set.
    /// </summary>
    System.Threading.Tasks.Task<IReadOnlyList<OgcLayerDto>> GetLayersAsync(
        string orgSlug, string dsSlug, string? folderPath = null, bool includeLocal = false,
        System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Resolve a single layer name. Returns null when not found (a local layer counts as
    /// not found unless <paramref name="includeLocal"/> is set).
    /// </summary>
    System.Threading.Tasks.Task<OgcLayerDto?> ResolveAsync(
        string orgSlug, string dsSlug, string layerName, string? folderPath = null, bool includeLocal = false,
        System.Threading.CancellationToken ct = default);

    /// <summary>
    /// Invalidate every OGC-related cache entry (layers + capabilities) for the given dataset.
    /// Call after add / remove / move / build operations so subsequent requests see fresh data.
    /// One cache write (a new generation of <see cref="CacheKeyAsync"/>), on every cache provider.
    /// </summary>
    System.Threading.Tasks.Task InvalidateAsync(string orgSlug, string dsSlug);

    /// <summary>
    /// The cache key of an OGC document of the dataset (capabilities and the like): stale as soon
    /// as <see cref="InvalidateAsync"/> runs for the dataset.
    /// </summary>
    System.Threading.Tasks.Task<string> CacheKeyAsync(string orgSlug, string dsSlug, string kind, string? detail = null,
        System.Threading.CancellationToken ct = default);
}
