using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Registry.Ports;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// libddb's raster info document (<c>DDBGetRasterInfo</c>, JSON) of the file an OGC raster layer reads: the one native
/// read WMS (the spectral indices, <see cref="IRasterIndexCatalog"/>) and WCS (size, bands, CRS) both consume, so a
/// raster is read once however many services ask. Each consumer parses the document for the members it needs; libddb
/// owns what they mean.
/// </summary>
public sealed class RasterInfoCache
{
    // The document does not change for given content and libddb version: kept a day. Only a JSON object is kept, so a
    // libddb that answered with something else is asked again
    private static readonly RecordCacheOptions DocumentCache = RecordCacheOptions.For(TimeSpan.FromDays(1)) with
    {
        CacheWhen = document => document is string text && IsJsonObject(text)
    };

    private readonly IDdbWrapper _ddb;
    private readonly IRecordCache _records;
    private readonly INativeWorkGate _gate;

    public RasterInfoCache(IDdbWrapper ddb, IRecordCache records, INativeWorkGate? gate = null)
    {
        _ddb = ddb;
        _records = records;
        _gate = gate ?? NativeWorkGate.Default;
    }

    /// <summary>
    /// The document of the file <paramref name="source"/> of the entry with content hash <paramref name="entryHash"/>.
    /// Read once per content, source file and libddb version, then cached. A read that fails is not kept: the
    /// exception libddb raised propagates (<see cref="Registry.Ports.DroneDB.DdbException"/>, a transient one among
    /// them, <see cref="Registry.Ports.DroneDB.DdbErrors.IsTransient"/>), and so does a cancellation of the caller;
    /// the next call asks libddb again. A document that is not a JSON object is returned but not kept.
    /// </summary>
    public Task<string> GetAsync(string entryHash, RasterSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // A caller that is gone starts no read: the record cache's flight would start before it saw the token
        ct.ThrowIfCancellationRequested();
        var key = CacheCategories.OgcRasterInfo(entryHash, source.Source, _ddb.GetVersion());

        return _records.GetOrCreateRecordAsync(key,
            token => _gate.RunAsync(NativeLane.Tiles, () => _ddb.GetRasterInfo(source.Path), token), DocumentCache, ct);
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
