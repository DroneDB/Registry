#nullable enable
namespace Registry.Web.Services.Ports;

/// <summary>A thumbnail to generate ahead of its first view.</summary>
/// <param name="OrgSlug">Organization of the dataset.</param>
/// <param name="DsSlug">Dataset.</param>
/// <param name="Hash">Entry hash (part of the cache key).</param>
/// <param name="LocalPath">The file on disk.</param>
/// <param name="Size">Thumbnail size in pixels.</param>
public sealed record ThumbnailPrecomputeRequest(string OrgSlug, string DsSlug, string Hash, string LocalPath, int Size);

/// <summary>
/// Pre-generates the thumbnails of uploaded images in the background at the lowest priority,
/// with a fixed number of workers (UPL-01 C.4). Replaces one unbounded task per upload.
/// </summary>
public interface IThumbnailPrecomputeQueue
{
    /// <summary>
    /// Queues a thumbnail. Returns false when the queue is full: the thumbnail is then
    /// generated on its first view instead.
    /// </summary>
    bool TryEnqueue(ThumbnailPrecomputeRequest request);
}
