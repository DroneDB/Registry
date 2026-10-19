using System.Collections.Generic;
using Registry.Ports.DroneDB;
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
        string orgSlug, string dsSlug, string? folderPath = null, bool includeLocal = false);

    /// <summary>
    /// Resolve a single layer name. Returns null when not found (a local layer counts as
    /// not found unless <paramref name="includeLocal"/> is set).
    /// </summary>
    System.Threading.Tasks.Task<OgcLayerDto?> ResolveAsync(
        string orgSlug, string dsSlug, string layerName, string? folderPath = null, bool includeLocal = false);

    /// <summary>
    /// Invalidate every OGC-related cache entry (layers + capabilities) for the given dataset.
    /// Call after add / remove / move / build operations so subsequent requests see fresh data.
    /// No-op when the configured cache backend does not support pattern scanning (e.g. in-memory).
    /// </summary>
    System.Threading.Tasks.Task InvalidateAsync(string orgSlug, string dsSlug);
}
