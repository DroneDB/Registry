#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Managers;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The spectral indices (NDVI, NDRE, NDWI, SAVI, EVI) a raster can render as a map layer, as libddb reports them
/// (<c>indexLayer</c> of <c>DDBGetRasterInfo</c>). libddb owns the rule; this is the one place Registry asks, so
/// the styles WMS advertises and the styles it accepts cannot differ.
/// </summary>
public interface IRasterIndexCatalog
{
    /// <summary>
    /// The indices libddb lists for the file <paramref name="source"/> of the entry with content hash
    /// <paramref name="entryHash"/>: upper-case, in libddb's order, empty when none (also when the raster cannot be
    /// read: the failure is logged and not remembered). Read once per content and libddb version, then cached.
    /// </summary>
    Task<IReadOnlyList<string>> GetAsync(string entryHash, RasterSource source, CancellationToken ct = default);
}
