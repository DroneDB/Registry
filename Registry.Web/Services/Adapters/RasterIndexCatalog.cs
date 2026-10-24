#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IRasterIndexCatalog"/> over libddb's raster info (<see cref="IRasterInfoCache"/>, the one cached read WCS
/// shares): the <c>indexLayer</c> member of the document of the raster.
/// </summary>
public sealed class RasterIndexCatalog : IRasterIndexCatalog
{
    private readonly IRasterInfoCache _info;
    private readonly ILogger<RasterIndexCatalog> _logger;

    public RasterIndexCatalog(IRasterInfoCache info, ILogger<RasterIndexCatalog> logger)
    {
        _info = info;
        _logger = logger;
    }

    /// <summary>
    /// The list of one raster, empty when it cannot be read (logged): a raster libddb cannot open renders no index, and
    /// must not fail the capabilities of the dataset. A canceled call is not a failure and propagates.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAsync(string entryHash, RasterSource source,
        CancellationToken ct = default)
    {
        try
        {
            var info = await _info.GetAsync(entryHash, source, ct);
            if (RasterIndexLayer.Parse(info) is { } list)
                return list;

            _logger.LogWarning(
                "libddb's raster info of {Path} has no \"indexLayer\" (a libddb older than 2.0?); it renders none",
                source.Path);
        }
        catch (DdbException ex)
        {
            _logger.LogWarning(ex, "Reading the spectral indices of {Path} failed; it renders none", source.Path);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "libddb's raster info of {Path} is not JSON; it renders none", source.Path);
        }

        return [];
    }
}
