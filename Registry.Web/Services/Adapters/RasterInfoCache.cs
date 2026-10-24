#nullable enable
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
/// <see cref="IRasterInfoCache"/> over <see cref="IDdbWrapper.GetRasterInfo"/>: one native read per content hash, source
/// file and libddb version (<see cref="CacheCategories.OgcRasterInfo"/>), on the tiles lane of
/// <see cref="INativeWorkGate"/> (a small call made on the way to a map or a coverage).
/// </summary>
public sealed class RasterInfoCache : IRasterInfoCache
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
