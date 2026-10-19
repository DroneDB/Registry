#nullable enable
using System;
using Microsoft.Extensions.Caching.Memory;
using Registry.Ports;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IDatasetSummaryProvider"/> over the process memory cache: one native DDBInfo per
/// dataset and index change, checked with two file stats (<c>IDDB.GetIndexFingerprint</c>).
/// </summary>
public class DatasetSummaryProvider : IDatasetSummaryProvider
{
    private static readonly TimeSpan UnusedExpiration = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;
    private readonly IDdbManager _ddbManager;

    public DatasetSummaryProvider(IMemoryCache cache, IDdbManager ddbManager)
    {
        _cache = cache;
        _ddbManager = ddbManager;
    }

    private sealed record CachedSummary(string Fingerprint, DatasetSummary Summary);

    private static string Key(Guid internalRef) => "dataset-summary:" + internalRef;

    public DatasetSummary Get(string orgSlug, Guid internalRef)
    {
        var ddb = _ddbManager.Get(orgSlug, internalRef);

        // Taken before reading the index: a write in between makes the next call read it again
        var fingerprint = ddb.GetIndexFingerprint();
        if (fingerprint != null && _cache.TryGetValue(Key(internalRef), out CachedSummary? cached) &&
            cached!.Fingerprint == fingerprint)
            return cached.Summary;

        var info = ddb.GetInfo();
        var summary = new DatasetSummary(info.Size, info.Properties);

        if (fingerprint != null)
            _cache.Set(Key(internalRef), new CachedSummary(fingerprint, summary),
                new MemoryCacheEntryOptions { SlidingExpiration = UnusedExpiration });

        return summary;
    }
}
