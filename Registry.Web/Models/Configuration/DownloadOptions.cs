#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>Download and export limits (keys of the <c>AppSettings</c> section).</summary>
public sealed class DownloadOptions
{
    public const long DefaultMaxExportSizeBytes = 1073741824; // 1 GiB

    /// <summary>
    /// Maximum number of concurrent downloads per user (or per IP for anonymous users).
    /// Null = unlimited (feature disabled). Minimum value: 1.
    /// </summary>
    public int? MaxConcurrentDownloadsPerUser { get; set; }

    /// <summary>
    /// When true, anonymous users may not download bulk archives (whole dataset, folders, or
    /// multi-file selections). Single-file downloads remain allowed.
    /// </summary>
    public bool DisableAnonymousBulkDownloads { get; set; }

    /// <summary>
    /// Maximum size in bytes of a GeoTIFF raster export (width × height × bytes per pixel × bands of
    /// the source). Null means unlimited. Default: 1 GiB.
    /// </summary>
    public long? MaxExportSizeBytes { get; set; } = DefaultMaxExportSizeBytes;
}
