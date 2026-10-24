#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Managers;

namespace Registry.Web.Services.Ports;

/// <summary>
/// libddb's raster info document (<c>DDBGetRasterInfo</c>, JSON) of the file an OGC raster layer reads: the one native
/// read WMS (the spectral indices, <see cref="IRasterIndexCatalog"/>) and WCS (size, bands, CRS) both consume, so a
/// raster is read once however many services ask. Each consumer parses the document for the members it needs; libddb
/// owns what they mean.
/// </summary>
public interface IRasterInfoCache
{
    /// <summary>
    /// The document of the file <paramref name="source"/> of the entry with content hash <paramref name="entryHash"/>.
    /// Read once per content, source file and libddb version, then cached. A read that fails is not kept: the
    /// exception libddb raised propagates (<see cref="Registry.Ports.DroneDB.DdbException"/>, a transient one among
    /// them, <see cref="Registry.Ports.DroneDB.DdbErrors.IsTransient"/>), and so does a cancellation of the caller;
    /// the next call asks libddb again. A document that is not a JSON object is returned but not kept.
    /// </summary>
    Task<string> GetAsync(string entryHash, RasterSource source, CancellationToken ct = default);
}
