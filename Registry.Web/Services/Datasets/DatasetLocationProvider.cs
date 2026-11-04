using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Stac;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Datasets;

/// <summary>
/// <see cref="IDatasetLocationProvider"/> over the record cache: the location of a dataset is read once per state of its
/// index (the fingerprint of the index files is part of the key, checked with two file stats) and kept for a long time,
/// since a change to the dataset moves the key. The cost of reading the geometry of a dataset of thousands of entries
/// is paid once per change, not once per map.
/// </summary>
public sealed class DatasetLocationProvider : IDatasetLocationProvider
{
    private static readonly RecordCacheOptions CacheOptions = RecordCacheOptions.For(TimeSpan.FromDays(7));

    // The entries that have a position: libddb's extent leaves out those with a position and no footprint
    private static readonly EntryType[] PositionedTypes = [EntryType.GeoImage, EntryType.GeoPanorama, EntryType.Geovideo];

    private readonly IDdbManager _ddbManager;
    private readonly IRecordCache _records;
    private readonly ILogger<DatasetLocationProvider> _logger;

    public DatasetLocationProvider(IDdbManager ddbManager, IRecordCache records, ILogger<DatasetLocationProvider> logger)
    {
        _ddbManager = ddbManager;
        _records = records;
        _logger = logger;
    }

    /// <summary>
    /// What the cache keeps: the cache does not store a null, and "this dataset has no location" is an answer worth
    /// keeping as much as a location.
    /// </summary>
    private sealed record CachedLocation(double[]? Bbox);

    // One unreadable dataset index must not take a map of many datasets down; a busy one is retried by the caller
    public Task<DatasetLocation?> GetAsync(string orgSlug, Guid internalRef, CancellationToken ct = default) =>
        PerDatasetRead.OrNullAsync(() => ReadOrCachedAsync(orgSlug, internalRef, ct),
            ex => _logger.LogWarning(ex, "No location for dataset {OrgSlug}/{InternalRef}", orgSlug, internalRef));

    private async Task<DatasetLocation?> ReadOrCachedAsync(string orgSlug, Guid internalRef, CancellationToken ct)
    {
        // Open, never Get: a dataset without a folder must not be created by showing a map
        var ddb = _ddbManager.Open(orgSlug, internalRef);

        // Taken before reading the index: a write in between moves the key of the next call
        var fingerprint = ddb.GetIndexFingerprint();
        if (fingerprint == null)
            return Read(ddb, orgSlug, internalRef); // the files cannot be stat-ed: nothing to key a record on

        var cached = await _records.GetOrCreateRecordAsync(CacheCategories.DatasetLocation(internalRef, fingerprint),
            _ => Task.FromResult(new CachedLocation(Read(ddb, orgSlug, internalRef)?.Bbox)), CacheOptions, ct);

        return cached.Bbox is null ? null : new DatasetLocation(cached.Bbox);
    }

    // The cost of a miss, which is what a map of many datasets pays on a cold cache (debug log: how long, and which way)
    private DatasetLocation? Read(IDDB ddb, string orgSlug, Guid internalRef)
    {
        var started = Stopwatch.GetTimestamp();
        var location = Read(ddb, out var fromEntries);
        _logger.LogDebug("Location of {OrgSlug}/{InternalRef} read in {ElapsedMs:F1} ms from the {Source}: {Found}",
            orgSlug, internalRef, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            fromEntries ? "entries" : "extent", location != null);
        return location;
    }

    private static DatasetLocation? Read(IDDB ddb, out bool fromEntries)
    {
        fromEntries = false;
        var extent = StacDocuments.SpatialBbox(ddb.GetStacData());
        if (IsLocation(extent))
            return new DatasetLocation(extent!);

        fromEntries = true;
        var box = new GeoJsonBbox();
        foreach (var entry in ddb.Find(EntryQuery.OfTypes(null, PositionedTypes)))
        {
            box.Add(entry.PointGeometry);
            box.Add(entry.PolygonGeometry);
        }

        return box.Result is { } bbox ? new DatasetLocation(bbox) : null;
    }

    // libddb writes all zeros for a dataset without extent
    private static bool IsLocation(double[]? bbox) => GeoJsonBbox.IsWithinWorld(bbox) && bbox!.Any(v => v != 0);
}
