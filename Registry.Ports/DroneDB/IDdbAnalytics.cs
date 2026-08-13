namespace Registry.Ports.DroneDB;

/// <summary>
/// Geospatial analytics computed over rasters: stockpile volume/detection and contour
/// generation. See ImproveParallelWrites plan, workstream 04 §7.
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
    string DetectStockpile(string path, double lat, double lon, double radiusMeters, float sensitivity);

    /// <summary>
    /// Auto-detect ALL stockpile footprints by full-DEM scan.
    /// </summary>
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
}
