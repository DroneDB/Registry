namespace Registry.Web.Services.Ports;

/// <summary>A thumbnail to generate ahead of its first view.</summary>
/// <param name="OrgSlug">Organization of the dataset.</param>
/// <param name="DsSlug">Dataset.</param>
/// <param name="Hash">Entry hash (part of the cache key).</param>
/// <param name="LocalPath">The file on disk.</param>
/// <param name="Size">Thumbnail size in pixels.</param>
public sealed record ThumbnailPrecomputeRequest(string OrgSlug, string DsSlug, string Hash, string LocalPath, int Size);
