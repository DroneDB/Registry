#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IRasterIndexCatalog"/> over <see cref="IDdbWrapper.GetRasterInfo"/>: one native read per content hash,
/// source file and libddb version (<see cref="CacheCategories.OgcRasterIndexLayer"/>), on the tiles lane of
/// <see cref="INativeWorkGate"/> (a small call made on the way to a map).
/// </summary>
public sealed class RasterIndexCatalog : IRasterIndexCatalog
{
    /// <summary>The list does not change for given content: kept a day.</summary>
    private static readonly RecordCacheOptions ListCache = RecordCacheOptions.For(TimeSpan.FromDays(1));

    private readonly IDdbWrapper _ddb;
    private readonly IRecordCache _records;
    private readonly INativeWorkGate _gate;
    private readonly ILogger<RasterIndexCatalog> _logger;

    public RasterIndexCatalog(IDdbWrapper ddb, IRecordCache records, ILogger<RasterIndexCatalog> logger,
        INativeWorkGate? gate = null)
    {
        _ddb = ddb;
        _records = records;
        _logger = logger;
        _gate = gate ?? NativeWorkGate.Default;
    }

    public async Task<IReadOnlyList<string>> GetAsync(string entryHash, RasterSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var key = CacheCategories.OgcRasterIndexLayer(entryHash, source.Source, _ddb.GetVersion());

        // A read that failed is null: not stored, the next request asks libddb again
        var list = await _records.GetOrCreateRecordAsync<string[]?>(key, token => ReadAsync(source.Path, token),
            ListCache, ct);
        return list ?? [];
    }

    /// <summary>
    /// The list of one raster, or null when it cannot be read (logged): a raster libddb cannot open renders no index,
    /// and must not fail the capabilities of the dataset. A canceled call is not a failure and propagates.
    /// </summary>
    private async Task<string[]?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var info = await _gate.RunAsync(NativeLane.Tiles, () => _ddb.GetRasterInfo(path), ct);
            var list = RasterIndexLayer.Parse(info);
            if (list != null) return [.. list];

            _logger.LogWarning(
                "libddb's raster info of {Path} has no \"indexLayer\" (a libddb older than 2.0?); it renders none", path);
        }
        catch (DdbException ex)
        {
            _logger.LogWarning(ex, "Reading the spectral indices of {Path} failed; it renders none", path);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "libddb's raster info of {Path} is not JSON; it renders none", path);
        }

        return null;
    }
}
