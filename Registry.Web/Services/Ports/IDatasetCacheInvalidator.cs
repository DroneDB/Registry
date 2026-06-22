using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Invalidates every cache entry associated with a dataset (tiles, thumbnails,
/// build-pending tracker, dataset thumbnail and OGC capabilities/layers).
/// </summary>
/// <remarks>
/// Extracted from <see cref="IObjectsManager"/> so background tools running on processing
/// nodes can clear dataset caches without depending on the full object-management stack.
/// Its concrete implementation depends only on cache services that are registered on every
/// host (web server and processing node).
/// </remarks>
public interface IDatasetCacheInvalidator
{
    /// <summary>
    /// Removes every cache entry associated with the given dataset.
    /// </summary>
    /// <param name="orgSlug">The organization slug.</param>
    /// <param name="dsSlug">The dataset slug.</param>
    /// <returns>A task that completes when all caches have been invalidated.</returns>
    Task InvalidateAllDatasetCachesAsync(string orgSlug, string dsSlug);
}
