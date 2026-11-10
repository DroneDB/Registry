namespace Registry.Web.Models.Configuration;

/// <summary>Download and export limits (keys of the <c>AppSettings</c> section).</summary>
public sealed class DownloadOptions
{
    public const long DefaultMaxExportSizeBytes = 1073741824; // 1 GiB

    /// <summary>The default of <see cref="MaxWcsCoverageSizePixels"/>.</summary>
    public const int DefaultMaxWcsCoverageSizePixels = 8192;

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

    /// <summary>
    /// The largest coverage a WCS GetCoverage returns (WCS 1.0, 1.1 and 2.0), in pixels on its longest side: a
    /// request for more is an OGC exception that states the limit, never a smaller image (FR-SHR-02, D-029). The
    /// WMS keeps its own limit (<see cref="Registry.Web.Utilities.Ogc.WmsValidator.MaxDim"/>). Default: 8192.
    /// </summary>
    public int MaxWcsCoverageSizePixels { get; set; } = DefaultMaxWcsCoverageSizePixels;
}
