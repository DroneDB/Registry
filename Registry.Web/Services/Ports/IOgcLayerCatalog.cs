using System.Collections.Generic;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO.Ogc;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Enumerates layers exposed via OGC for a dataset (raster + vector + optionally multi-layer GPKG inner layers).
/// Cached for <see cref="OgcLayerCatalogCacheMinutes"/> in Redis.
/// </summary>
public interface IOgcLayerCatalog
{
    /// <summary>Get all OGC layers in the given dataset, optionally restricted to a sub-folder.</summary>
    System.Threading.Tasks.Task<IReadOnlyList<OgcLayerDto>> GetLayersAsync(
        string orgSlug, string dsSlug, string? folderPath = null);

    /// <summary>Resolve a single layer name. Returns null when not found.</summary>
    System.Threading.Tasks.Task<OgcLayerDto?> ResolveAsync(
        string orgSlug, string dsSlug, string layerName, string? folderPath = null);
}
