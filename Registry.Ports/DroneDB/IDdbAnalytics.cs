using System;
using System.Threading;
namespace Registry.Ports.DroneDB;

/// <summary>
/// Geospatial analytics computed over rasters: stockpile volume/detection and contour
/// generation.
/// </summary>
public interface IDdbAnalytics
{
    /// <summary>
    /// Calculate stockpile volume (cut/fill/net) over a polygon on a DEM raster
    /// </summary>
    string CalculateVolume(string path, string polygonGeoJson, string baseMethod, double flatElevation);

    /// <summary>
    /// Auto-detect a stockpile footprint starting from a click on the raster
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">Nothing to detect: the click is outside the raster, the
    /// search region has no data or no pile, or the search window is too small.</exception>
    string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity);

    /// <summary>
    /// Auto-detect ALL stockpile footprints by full-DEM scan (no pile found is an empty list).
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">The raster has no valid elevation.</exception>
    string DetectAllStockpiles(string path, float sensitivity, double minAreaM2, int maxResults);

    /// <summary>
    /// Generate contour lines from a single-band elevation raster (DEM/DSM/DTM).
    /// Returns a GeoJSON FeatureCollection of LineString features.
    /// </summary>
    string GenerateContours(string path,
                            double? interval,
                            int? count,
                            double baseOffset = 0.0,
                            double? minElev = null,
                            double? maxElev = null,
                            double simplifyTolerance = 0.0,
                            int bandIndex = 1);

    /// <summary>
    /// <see cref="GenerateContours(string, double?, int?, double, double?, double?, double, int)"/> with the options of the
    /// values (the NoData override). The levels, the range and the elevations are in the units of the band.
    /// </summary>
    string GenerateContours(string path,
                            double? interval,
                            int? count,
                            double baseOffset,
                            double? minElev,
                            double? maxElev,
                            double simplifyTolerance,
                            int bandIndex,
                            RasterAnalysisOptions? options);

    /// <summary>
    /// <see cref="GenerateContours(string, double?, int?, double, double?, double?, double, int, RasterAnalysisOptions?)"/> that
    /// reports its progress (fraction 0..1 and the phase: "statistics", "levels", "lines") and stops when
    /// <paramref name="cancellationToken"/> is canceled (<see cref="DdbCanceledException"/>). A libddb older than 2.3 reports no
    /// progress (the cancel still stops it). The output says when the lines were cut at the cap (<c>truncated</c>,
    /// <c>featureCap</c>, libddb 2.3).
    /// </summary>
    string GenerateContours(string path,
                            double? interval,
                            int? count,
                            double baseOffset,
                            double? minElev,
                            double? maxElev,
                            double simplifyTolerance,
                            int bandIndex,
                            RasterAnalysisOptions? options,
                            Action<double, string?>? progress,
                            CancellationToken cancellationToken);
}
