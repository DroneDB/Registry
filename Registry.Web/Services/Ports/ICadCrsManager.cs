#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Sets the coordinate system of a CAD drawing (DDB-12): writes the drawing's same-name .prj,
/// which DroneDB reads before the drawing's own GEODATA and rebuilds the drawing for.
/// </summary>
public interface ICadCrsManager
{
    /// <summary>
    /// Gives the drawing at <paramref name="path"/> the coordinate system <paramref name="crs"/>: an
    /// authority code ("EPSG:32632"), WKT or PROJ string, or "local" to keep it in drawing
    /// coordinates. Returns the .prj entry.
    /// </summary>
    Task<EntryDto> SetAsync(string orgSlug, string dsSlug, string path, string crs, CancellationToken ct = default);
}
