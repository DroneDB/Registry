using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Registry.Common.Model;
using Registry.Ports.DroneDB;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface IObjectsManager
{
    Task<IEnumerable<EntryDto>> List(string orgSlug, string dsSlug, string path = null, bool recursive = false, EntryType? type = null);
    Task<IEnumerable<EntryDto>> Search(string orgSlug, string dsSlug, string query = null, string path = null,
        bool recursive = true, EntryType? type = null);
    Task<StorageEntryDto> Get(string orgSlug, string dsSlug, string path);
    Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, byte[] data);
    Task<EntryDto> AddNew(string orgSlug, string dsSlug, string path, Stream stream = null);
    Task<Entry> Move(string orgSlug, string dsSlug, string source, string dest);
    Task Delete(string orgSlug, string dsSlug, string path);
    Task DeleteAll(string orgSlug, string dsSlug);
    Task<FileStreamDescriptor> DownloadStream(string orgSlug, string dsSlug, string[] paths);
    Task<StorageDataDto> GenerateThumbnailData(string orgSlug, string dsSlug, string? path, int? size, bool recreate = false);
    Task<StorageDataDto> GenerateTileData(string orgSlug, string dsSlug, string path, int tz, int tx, int ty, bool retina);
    Task<FileStreamDescriptor> GetDdb(string orgSlug, string dsSlug);
    Task Build(string orgSlug, string dsSlug, string path, bool force = false);
    Task<string> GetBuildFile(string orgSlug, string dsSlug, string hash, string path);
    Task<bool> CheckBuildFile(string orgSlug, string dsSlug, string hash, string path);
    Task<EntryType?> GetEntryType(string orgSlug, string dsSlug, string path);

    Task Transfer(string sourceOrgSlug, string sourceDsSlug, string sourcePath, string destOrgSlug,
        string destDsSlug, string destPath = null, bool overwrite = false);

    Task<IEnumerable<BuildJobDto>> GetBuilds(string orgSlug, string dsSlug, int page = 1, int pageSize = 50);
    Task<int> ClearCompletedBuilds(string orgSlug, string dsSlug);
    Task Delete(string orgSlug, string dsSlug, string[] paths);
    Task<DeleteBatchResponse> DeleteBatch(string orgSlug, string dsSlug, string[] paths);

    /// <summary>
    /// Invalidates all cached data for a dataset (tiles, thumbnails, dataset thumbnail, build-pending).
    /// </summary>
    Task InvalidateAllDatasetCaches(string orgSlug, string dsSlug);

    /// <summary>Get raster info including bands, sensor profile, and presets</summary>
    Task<string> GetRasterInfo(string orgSlug, string dsSlug, string path);

    /// <summary>Get raster statistics and histogram for a band or formula</summary>
    Task<string> GetRasterMetadata(string orgSlug, string dsSlug, string path, string? formula = null, string? bandFilter = null);

    /// <summary>Generate thumbnail with extended visualization params</summary>
    Task<StorageDataDto> GenerateThumbnailDataEx(string orgSlug, string dsSlug, string path, int? size,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>Generate tile with extended visualization params</summary>
    Task<StorageDataDto> GenerateTileDataEx(string orgSlug, string dsSlug, string path,
        int tz, int tx, int ty, bool retina,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>Validate merge-multispectral inputs</summary>
    Task<string> ValidateMergeMultispectral(string orgSlug, string dsSlug, string[] paths);

    /// <summary>Preview merge-multispectral result</summary>
    Task<byte[]> PreviewMergeMultispectral(string orgSlug, string dsSlug, string[] paths, string? previewBands = null, int thumbSize = 512);

    /// <summary>Merge single-band rasters into multi-band COG</summary>
    Task MergeMultispectral(string orgSlug, string dsSlug, string[] paths, string outputPath);

    /// <summary>Export raster with visualization params applied as GeoTIFF</summary>
    Task<StorageDataDto> ExportRaster(string orgSlug, string dsSlug, string path,
        string? preset = null, string? bands = null, string? formula = null,
        string? bandFilter = null, string? colormap = null, string? rescale = null);

    /// <summary>
    /// Estimates the output size (in bytes) of a GeoTIFF export for the given raster path.
    /// Uses raw input data size (width × height × bytesPerPixel × bandCount) as a conservative upper bound.
    /// </summary>
    Task<long> EstimateExportSize(string orgSlug, string dsSlug, string path);

    /// <summary>Get thermal info including calibration, temperature range, and dimensions</summary>
    Task<string> GetThermalInfo(string orgSlug, string dsSlug, string path);

    /// <summary>Get temperature at a specific pixel location</summary>
    Task<string> GetThermalPoint(string orgSlug, string dsSlug, string path, int x, int y);

    /// <summary>Get temperature statistics for a rectangular area</summary>
    Task<string> GetThermalAreaStats(string orgSlug, string dsSlug, string path, int x0, int y0, int x1, int y1);

    /// <summary>Check if a masked version of the orthophoto already exists</summary>
    Task<MaskBordersCheckResponseDto> CheckMaskedFileExists(string orgSlug, string dsSlug, string path);

    /// <summary>Mask orthophoto borders making them transparent</summary>
    Task MaskBorders(string orgSlug, string dsSlug, string path, int nearDist, bool white);
}