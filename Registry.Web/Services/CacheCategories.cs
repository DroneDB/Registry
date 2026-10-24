#nullable enable
namespace Registry.Web.Services;

/// <summary>
/// Every cache key format and cache category of Registry (RQ-12): what a key looks like is decided here, so the
/// code that writes an entry and the code that invalidates it cannot drift apart.
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

    // ---- Distributed-cache keys ------------------------------------------------------------------

    /// <summary>The generation of a dataset's OGC documents (<c>ICacheGenerations</c>).</summary>
    public static string OgcGeneration(string orgSlug, string dsSlug) => $"ogc-gen-{orgSlug}-{dsSlug}";

    /// <summary>An OGC document of a dataset (capabilities, layer list) in a generation.</summary>
    /// <param name="orgSlug">Organization slug.</param>
    /// <param name="dsSlug">Dataset slug.</param>
    /// <param name="generation">The dataset's current OGC generation.</param>
    /// <param name="kind">What the document is, e.g. <c>caps-wms-v2-1.3.0</c> or <c>layers</c>.</param>
    /// <param name="detail">What else selects it (folder, sections), or null.</param>
    public static string OgcDocument(string orgSlug, string dsSlug, string generation, string kind, string? detail) =>
        $"ogc-{kind}-{orgSlug}-{dsSlug}-g{generation}-{detail ?? string.Empty}";

    /// <summary>
    /// The raster probe (size, bands) of the file a WCS coverage reads, the build's artifact or the entry's own file
    /// (<paramref name="source"/> names which). A probe does not change for given content (the hash).
    /// </summary>
    public static string OgcRasterProbe(string entryHash, string source) => $"ogc-wcs-probe-{entryHash}-{source}";

    /// <summary>
    /// The spectral indices libddb can render from the file a WMS layer reads (<paramref name="source"/> names which:
    /// the build's artifact or the entry's own file). They depend on the content (the hash) and on libddb's rule, so
    /// the libddb version is part of the key: a libddb that changes the rule never reads a list its predecessor kept.
    /// </summary>
    public static string OgcRasterIndexLayer(string entryHash, string source, string ddbVersion) =>
        $"ogc-wms-index-layer-{ddbVersion}-{entryHash}-{source}";

    /// <summary>The generation of every STAC listing (landing page, collections): one for the whole catalog.</summary>
    public const string StacGeneration = "stac-gen";

    /// <summary>The STAC landing page of a caller in a STAC generation.</summary>
    public static string StacLanding(string generation, string userName) => $"stac-landing-g{generation}-{userName}";

    /// <summary>A page of the STAC collections listing of a caller in a STAC generation.</summary>
    public static string StacCollections(string generation, string userName, int limit, int offset) =>
        $"stac-collections-g{generation}-{userName}-{limit}-{offset}";

    /// <summary>The read-only lookup of a dataset by its slugs (with its organization's members).</summary>
    public static string DatasetLookup(string orgSlug, string dsSlug) => $"ds-lookup:{orgSlug}/{dsSlug}";

    /// <summary>The receipt of an upload request (its outcome, for a client retrying after a lost answer).</summary>
    public static string UploadReceipt(string orgSlug, string dsSlug, string uploadId) =>
        $"upload-receipt-{orgSlug}-{dsSlug}-{uploadId}";

    /// <summary>The canonical first page of a dataset's task list.</summary>
    public static string TasksList(string orgSlug, string dsSlug) => $"{MagicStrings.TasksListCacheSeed}:{orgSlug}/{dsSlug}";

    // ---- Process-memory keys -------------------------------------------------------------------

    /// <summary>A push session read from its file (<paramref name="sessionFile"/>: the file's full path).</summary>
    public static string PushSession(string sessionFile) => "push-session:" + sessionFile;
}
