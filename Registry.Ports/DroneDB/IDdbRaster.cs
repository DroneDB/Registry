using System;
using System.Threading;

namespace Registry.Ports.DroneDB;

/// <summary>
/// Raster analysis and visualization: tiles, thumbnails, raster info/stats, export, borders
/// masking, multispectral merge and raster alignment. See ImproveParallelWrites plan,
/// workstream 04 §7.
/// </summary>
public interface IDdbRaster
{
    byte[] GenerateThumbnail(string imagePath, int size);
    byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash);

    /// <summary>Generate a tile with a specific raster output format ("png" or "jpeg").</summary>
    byte[] GenerateTile(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash, string outputFormat);

    /// <summary>
    /// Get raster info including bands, detected sensor, and presets
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
    /// Generate tile with extended visualization params
    /// </summary>
    byte[] GenerateTileEx(string inputPath, int tz, int tx, int ty, bool retina, string inputPathHash,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Validate merge-multispectral inputs
    /// </summary>
    string ValidateMergeMultispectral(string[] paths);

    /// <summary>
    /// Preview merge-multispectral result
    /// </summary>
    byte[] PreviewMergeMultispectral(string[] paths, string? previewBands = null, int thumbSize = 512);

    /// <summary>
    /// Merge single-band rasters into multi-band COG
    /// </summary>
    void MergeMultispectral(string[] paths, string outputCog);

    /// <summary>
    /// Validate that source and reference rasters are compatible for alignment
    /// </summary>
    string ValidateAlignRaster(string sourcePath, string referencePath);

    /// <summary>
    /// Align a source GeoTIFF to a reference GeoTIFF and write the output COG
    /// </summary>
    string AlignRaster(string sourcePath, string referencePath, string outputPath, string mode = "similarity");

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
    /// Get raster value (temperature/elevation/etc.) at a specific pixel location
    /// </summary>
    string GetRasterPointValue(string path, int x, int y);

    /// <summary>
    /// Get raster value statistics for a rectangular area
    /// </summary>
    string GetRasterAreaStats(string path, int x0, int y0, int x1, int y1);

    /// <summary>
    /// Sample raster values along a GeoJSON LineString (WGS84)
    /// </summary>
    string GetRasterProfile(string path, string geoJsonLineString, int samples);

    /// <summary>
    /// Mask orthophoto borders making them transparent
    /// </summary>
    void MaskBorders(string input, string output, int nearDist = 15, bool white = false);
}
