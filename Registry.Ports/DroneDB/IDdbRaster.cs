using System;
using System.Threading;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Raster analysis and visualization: tiles, thumbnails, raster info/stats, export, borders
/// masking, multispectral merge and raster alignment.
/// </summary>
public interface IDdbRaster
{
    byte[] GenerateThumbnail(string imagePath, int size);

    /// <summary>Generate a tile with a specific raster output format ("png" or "jpeg").</summary>
    byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash, string outputFormat);

    /// <summary>
    /// Get raster info including bands, detected sensor, and presets, and <c>indexLayer</c>: the spectral
    /// indices the raster can render as a map layer (<see cref="RasterIndexLayer"/>)
    /// </summary>
    string GetRasterInfo(string path);

    /// <summary>
    /// Get raster statistics and histogram for a band or formula
    /// </summary>
    string GetRasterMetadata(string path, string? formula = null, string? bandFilter = null);

    /// <summary>
    /// Generate thumbnail with extended visualization params
    /// </summary>
    byte[] GenerateThumbnailEx(string imagePath, int size, string? preset = null,
        string? bands = null, string? formula = null, string? bandFilter = null,
        string? colormap = null, string? rescale = null);

    /// <summary>
    /// Generate thumbnail with extended visualization params and a NoData value that replaces the one of the bands
    /// (FR-RAS-31): a pixel equal to it is transparent and out of the stretch.
    /// </summary>
    byte[] GenerateThumbnailEx(string imagePath, int size, string? preset, string? bands, string? formula,
        string? bandFilter, string? colormap, string? rescale, double? nodata);

    /// <summary>
    /// Generate thumbnail with extended visualization params and the options of the render: the NoData value
    /// (FR-RAS-31) and the unit of the rescale of a thermal formula (<see cref="RasterAnalysisOptions.RescaleUnit"/>,
    /// D-022).
    /// </summary>
    byte[] GenerateThumbnailEx(string imagePath, int size, string? preset, string? bands, string? formula,
        string? bandFilter, string? colormap, string? rescale, RasterAnalysisOptions? options);

    /// <summary>
    /// Generate tile with extended visualization params
    /// </summary>
    byte[] GenerateTileEx(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Generate tile with extended visualization params and a NoData value that replaces the one of the bands
    /// (FR-RAS-31).
    /// </summary>
    byte[] GenerateTileEx(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash,
        string? preset, string? bands, string? formula, string? bandFilter, string? colormap, string? rescale,
        double? nodata);

    /// <summary>
    /// Generate tile with extended visualization params and the options of the render: the NoData value (FR-RAS-31)
    /// and the unit of the rescale of a thermal formula (<see cref="RasterAnalysisOptions.RescaleUnit"/>, D-022).
    /// </summary>
    byte[] GenerateTileEx(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash,
        string? preset, string? bands, string? formula, string? bandFilter, string? colormap, string? rescale,
        RasterAnalysisOptions? options);

    /// <summary>
    /// Validate merge-multispectral inputs
    /// </summary>
    string ValidateMergeMultispectral(string[] paths);

    /// <summary>
    /// Preview merge-multispectral result
    /// </summary>
    byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512);

    /// <summary>
    /// Merge single-band rasters into a multi-band GeoTIFF. An output that exists is replaced only with
    /// <paramref name="overwrite"/>, in one rename once the merge is complete (a failed call leaves it as it was);
    /// without it, libddb refuses it.
    /// </summary>
    void MergeMultispectral(string[] paths, string outputCog, bool overwrite = false);

    /// <summary>
    /// Validate that source and reference rasters are compatible for alignment
    /// </summary>
    string ValidateAlignRaster(string sourcePath, string referencePath);

    /// <summary>
    /// Align a source GeoTIFF to a reference GeoTIFF and write the aligned GeoTIFF. An output that exists is replaced
    /// only with <paramref name="overwrite"/>, in one rename once it is complete (a failed call leaves it as it was);
    /// without it, libddb refuses it.
    /// </summary>
    string AlignRaster(string sourcePath, string referencePath, string outputPath, string mode = "similarity",
        bool overwrite = false);

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF
    /// </summary>
    void ExportRaster(string inputPath, string outputPath,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Export raster with visualization params applied as GeoTIFF using the
    /// block-windowed implementation (bounded peak memory), reporting incremental
    /// progress and honoring cooperative cancellation.
    /// </summary>
    void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale, int tileSize,
        Action<double, string?>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get raster value info (min/max/unit/dimensions), including thermal calibration if applicable
    /// </summary>
    string GetRasterValueInfo(string path);

    /// <summary>
    /// <see cref="GetRasterValueInfo(string)"/> with the options of the values: the exact range of the pixels
    /// that have a value in the units of the band, the NoData value that applies (<c>nodata</c>, <c>nodataInvalid</c>), the
    /// pixel counts for the export estimate and the default <c>stretch</c> (D-041).
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">A formula that does not exist or that the raster cannot compute.</exception>
    string GetRasterValueInfo(string path, RasterAnalysisOptions? options);

    /// <summary>
    /// Get raster value (temperature/elevation/etc.) at a specific pixel location
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">The pixel is outside the raster.</exception>
    string GetRasterPointValue(string path, int x, int y);

    /// <summary>
    /// <see cref="GetRasterPointValue(string, int, int)"/> with the options of the values (the NoData override, the formula):
    /// the pixel coordinates of an R-JPEG's raw thermal grid (§7.1). Options that libddb cannot apply to pixel
    /// coordinates are refused, never dropped.
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">The pixel is outside the raster, or the options cannot be applied.</exception>
    string GetRasterPointValue(string path, int x, int y, RasterAnalysisOptions? options);

    /// <summary>
    /// The raster value at a WGS 84 point: the pixel of the file under it, found by libddb's one geo-to-pixel conversion
    /// (D-021), in the units of the band.
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">The point is outside the raster, or its pixel has no value.</exception>
    string GetRasterPointValue(string path, double lon, double lat, RasterAnalysisOptions? options = null);

    /// <summary>
    /// Value statistics of the pixels whose centre is inside a WGS 84 rectangle (<c>minLon, minLat, maxLon, maxLat</c>).
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">No pixel of the rectangle has a value.</exception>
    string GetRasterAreaStats(string path, double minLon, double minLat, double maxLon, double maxLat,
        RasterAnalysisOptions? options = null);

    /// <summary>
    /// Get raster value statistics for a rectangular area: its part inside the raster
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">No pixel of the area has a value: it is outside the raster, or
    /// its pixels are all no data.</exception>
    string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1);

    /// <summary>
    /// <see cref="GetRasterAreaStats(string, int, int, int, int)"/> with the options of the values (the NoData override, the
    /// formula). Options that libddb cannot apply to pixel coordinates are refused, never dropped.
    /// </summary>
    /// <exception cref="DdbInvalidArgumentsException">No pixel of the area has a value, or the options cannot be applied.</exception>
    string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1, RasterAnalysisOptions? options);

    /// <summary>
    /// Sample raster values along a GeoJSON LineString (WGS84)
    /// </summary>
    string GetRasterProfile(string path, string geoJsonLineString, int samples);

    /// <summary><see cref="GetRasterProfile(string, string, int)"/> with the options of the values (the NoData override).</summary>
    string GetRasterProfile(string path, string geoJsonLineString, int samples, RasterAnalysisOptions? options);

    /// <summary>
    /// <see cref="ExportRaster(string, string, string?, string?, string?, string?, string?, string?, int, Action{double, string?}?, CancellationToken)"/>
    /// with a NoData value that replaces the one of the bands (FR-RAS-31).
    /// </summary>
    void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale, int tileSize, double? nodata,
        Action<double, string?>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ExportRaster(string, string, string?, string?, string?, string?, string?, string?, int, double?, Action{double, string?}?, CancellationToken)"/>
    /// with the options of the values: the NoData value, the unit of the rescale and the WGS 84 window
    /// (<see cref="RasterAnalysisOptions.Bbox"/>) to export instead of the whole raster (FR-RAS-16).
    /// </summary>
    void ExportRaster(string inputPath, string outputPath,
        string? preset, string? bands, string? formula, string? bandFilter,
        string? colormap, string? rescale, int tileSize, RasterAnalysisOptions? options,
        Action<double, string?>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mask orthophoto borders making them transparent. An output that exists is replaced only with
    /// <paramref name="overwrite"/>, in one rename once the mask is complete (a failed or cancelled call leaves it as
    /// it was); without it, libddb refuses it.
    /// </summary>
    void MaskBorders(string input, string output, int nearDist = 15, bool white = false, bool overwrite = false);
}
