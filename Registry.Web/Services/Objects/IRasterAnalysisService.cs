#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Objects;

/// <summary>
/// Measurements on a raster entry (read from its build artifact once built): its description and statistics,
/// values at points, along lines and over areas, stockpile volumes and footprints, contour lines, and a
/// GeoTIFF export. Results are libddb's JSON, passed through.
/// </summary>
public interface IRasterAnalysisService
{
    /// <summary>Bands, sensor profile and visualization presets.</summary>
    Task<string> GetRasterInfoAsync(string orgSlug, string dsSlug, string path, CancellationToken ct = default);

    /// <summary>Statistics and histogram of a band or of a band formula.</summary>
    Task<string> GetRasterMetadataAsync(string orgSlug, string dsSlug, string path, string? formula = null,
        string? bandFilter = null, CancellationToken ct = default);

    /// <summary>Value range, unit and dimensions (with the thermal calibration when there is one).</summary>
    Task<string> GetRasterValueInfoAsync(string orgSlug, string dsSlug, string path, CancellationToken ct = default);

    /// <summary>The value (temperature, elevation, ...) at the pixel <paramref name="x"/>, <paramref name="y"/>.</summary>
    Task<string> GetRasterPointValueAsync(string orgSlug, string dsSlug, string path, int x, int y,
        CancellationToken ct = default);

    /// <summary>Value statistics over a pixel rectangle.</summary>
    Task<string> GetRasterAreaStatsAsync(string orgSlug, string dsSlug, string path, int x0, int y0, int x1, int y1,
        CancellationToken ct = default);

    /// <summary>Values sampled along a GeoJSON LineString (WGS84).</summary>
    Task<string> GetRasterProfileAsync(string orgSlug, string dsSlug, string path, string geoJsonLineString,
        int samples, CancellationToken ct = default);

    /// <summary>Cut, fill and net volume over a GeoJSON polygon of an elevation raster.</summary>
    Task<string> CalculateVolumeAsync(string orgSlug, string dsSlug, string path, string polygonGeoJson,
        string? baseMethod, double flatElevation, CancellationToken ct = default);

    /// <summary>The footprint of the stockpile around a clicked location.</summary>
    Task<string> DetectStockpileAsync(string orgSlug, string dsSlug, string path, double lat, double lon,
        double radiusMeters, float sensitivity, CancellationToken ct = default);

    /// <summary>The footprints of every stockpile of the elevation raster (a full scan).</summary>
    Task<string> DetectAllStockpilesAsync(string orgSlug, string dsSlug, string path, float sensitivity,
        double minAreaM2, int maxResults, CancellationToken ct = default);

    /// <summary>Contour lines of a single-band elevation raster (DEM/DSM/DTM).</summary>
    Task<string> GenerateContoursAsync(string orgSlug, string dsSlug, string path, ContourOptions options,
        CancellationToken ct = default);

    /// <summary>The raster as a GeoTIFF with the visualization applied, within the export size limit.</summary>
    Task<StorageDataDto> ExportRasterAsync(string orgSlug, string dsSlug, string path,
        RasterVisualization visualization, CancellationToken ct = default);
}

/// <summary>
/// The contour lines asked for: an elevation step (<paramref name="Interval"/>, wins when both are set) or a
/// number of lines, an offset, an elevation range, a simplification tolerance and the band.
/// </summary>
public sealed record ContourOptions(
    double? Interval, int? Count, double BaseOffset, double? MinElev, double? MaxElev, double SimplifyTolerance,
    int BandIndex);
