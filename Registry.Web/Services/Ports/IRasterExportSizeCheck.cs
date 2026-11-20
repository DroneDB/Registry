using System.Threading;
using System.Threading.Tasks;
using Registry.Ports.DroneDB;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The export size limit of a styled raster (D-023, FR-RAS-16) for the submit of the raster-export task: the check of
/// the direct export (<see cref="Objects.RasterAnalysisService.EnsureExportSizeWithinLimitAsync"/>, with the one estimate
/// of the value info, <see cref="Objects.RasterAnalysisService.EstimateExportBytesAsync"/>), so a task is refused for the
/// size the Hub shows. A singleton: a heavy tool holds it.
/// </summary>
public interface IRasterExportSizeCheck
{
    /// <summary>Refuses an export of <paramref name="path"/> whose estimate is over the export limit.</summary>
    /// <exception cref="Exceptions.BadRequestException">The estimate is over the limit.</exception>
    Task EnsureWithinLimitAsync(string orgSlug, string dsSlug, string path, RasterAnalysisOptions? options,
        CancellationToken ct = default);
}
