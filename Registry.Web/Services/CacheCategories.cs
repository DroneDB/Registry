namespace Registry.Web.Services;

/// <summary>
/// Centralizes the construction of cache category keys.
/// All cache category formatting logic should live here to avoid
/// spreading knowledge of the key format across multiple classes.
/// </summary>
public static class CacheCategories
{
    /// <summary>
    /// Returns the cache category for per-file operations on a dataset (thumbnails, tiles, build-pending).
    /// Format: "{orgSlug}/{dsSlug}"
    /// </summary>
    public static string ForDataset(string orgSlug, string dsSlug) => $"{orgSlug}/{dsSlug}";

    /// <summary>
    /// Returns the cache category for dataset-level thumbnail operations.
    /// Format: "{orgSlug}/{dsSlug}/ds-thumb"
    /// </summary>
    public static string ForDatasetThumbnail(string orgSlug, string dsSlug) => $"{orgSlug}/{dsSlug}/ds-thumb";
}
