using System;

namespace Registry.Web.Models.Configuration;

/// <summary>The distributed cache and the expirations of the cached data (keys of the <c>AppSettings</c> section).</summary>
public sealed class CachingOptions
{
    /// <summary>Cache provider; null uses the in-process memory cache.</summary>
    public CacheProvider? CacheProvider { get; set; }

    /// <summary>Thumbnails cache expiration (at least 1 minute when set).</summary>
    public TimeSpan? ThumbnailsCacheExpiration { get; set; }

    /// <summary>Tiles cache expiration (at least 1 minute when set).</summary>
    public TimeSpan? TilesCacheExpiration { get; set; }

    /// <summary>Dataset visibility cache expiration (at least 1 minute when set).</summary>
    public TimeSpan? DatasetVisibilityCacheExpiration { get; set; }

    /// <summary>
    /// How long a user's authorization facts (exists, admin, deactivated) are reused across
    /// requests. Role changes and deletions made by this instance take effect at once; the
    /// expiration bounds changes made elsewhere (other instances, direct database edits).
    /// Zero disables the cache. Default: 30 seconds.
    /// </summary>
    public TimeSpan? AuthorizationCacheExpiration { get; set; }

    /// <summary>
    /// How long a dataset lookup by slug (dataset + organization row) is reused across requests.
    /// Changes saved by this instance take effect at once; the expiration bounds changes made
    /// elsewhere (processing nodes, direct database edits). Zero disables it. Default: 30 seconds.
    /// </summary>
    public TimeSpan? DatasetLookupCacheExpiration { get; set; }

    /// <summary>Clear cache interval (at least 1 minute when set).</summary>
    public TimeSpan? ClearCacheInterval { get; set; }
}
